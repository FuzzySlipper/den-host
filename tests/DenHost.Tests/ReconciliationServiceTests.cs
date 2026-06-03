using DenHost.Configuration;
using DenHost.Harness;
using DenHost.Host;
using DenHost.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class ReconciliationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ReconciliationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private (ReconciliationService Service, RunRegistry Registry, RuntimeOptions Runtime) Build()
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "den-host-test",
            Host = "h-1",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var registry = new RunRegistry(runtime, identity, NullLogger<RunRegistry>.Instance);
        var service = new ReconciliationService(registry, runtime, NullLogger<ReconciliationService>.Instance);
        return (service, registry, runtime);
    }

    private LocalRunRecord MakeRecord(string localRunId, int? pid, LocalRunState state, bool withUncleanMarker = true)
    {
        return new LocalRunRecord(
            LocalRunId: localRunId,
            AssignmentId: 42,
            HarnessKind: HarnessModuleKind.Hermes,
            HarnessModuleName: "h",
            ProcessId: pid,
            LogFilePath: "/tmp/log",
            StartedAt: DateTimeOffset.UtcNow,
            State: state,
            RunDir: "");
    }

    [Fact]
    public async Task Branch1_RunningProcessActiveAssignment_ReAdopts()
    {
        var (service, registry, _) = Build();
        // Use the test process's own pid -- it is alive for the duration of the test.
        var rec = await registry.RegisterAsync(
            MakeRecord("run-1", Environment.ProcessId, LocalRunState.Running),
            CancellationToken.None);

        var reports = await service.ReconcileOnceAsync(CancellationToken.None);

        Assert.Single(reports);
        Assert.Equal(ReconciliationOutcome.ReAdopted, reports[0].Outcome);
        Assert.Equal("active", reports[0].AssignmentStateObserved);
    }

    [Fact]
    public async Task Branch2_MissingProcessActiveAssignment_QuarantinesAndWritesEvidence()
    {
        var (service, registry, runtime) = Build();
        // Set up a run with no marker (no unclean-shutdown signal) and a
        // gone process. This is the "missing process + running assignment"
        // branch: the host intended to host the run but the process is gone.
        var rec = await registry.RegisterAsync(
            MakeRecord("run-1", int.MaxValue, LocalRunState.Running),
            CancellationToken.None);
        // Remove the unclean-shutdown marker that RegisterAsync writes so
        // the test exercises the no-marker branch of the stale-assignment
        // path. This is realistic: the previous host lifetime ended cleanly
        // for this run but the assignment is still considered active in Core.
        var markerPath = Path.Combine(rec.RunDir, RunRegistry.UncleanShutdownMarkerFileName);
        File.Delete(markerPath);

        var reports = await service.ReconcileOnceAsync(CancellationToken.None);

        Assert.Single(reports);
        Assert.Equal(ReconciliationOutcome.StaleAssignmentActive, reports[0].Outcome);
        Assert.NotNull(reports[0].EvidencePath);
        Assert.True(File.Exists(reports[0].EvidencePath!));
        Assert.Contains("reconciliation-run-1", reports[0].EvidencePath!);
    }

    [Fact]
    public async Task Branch3_ProcessExistsTerminalAssignment_Terminates()
    {
        var (service, registry, _) = Build();
        // Spawn a sleep subprocess so we have a real live pid to kill.
        using var dummyProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sleep",
            ArgumentList = { "30" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(dummyProcess);
        await Task.Delay(50);

        try
        {
            var rec = await registry.RegisterAsync(
                MakeRecord("run-1", dummyProcess!.Id, LocalRunState.Stopped),
                CancellationToken.None);

            var reports = await service.ReconcileOnceAsync(CancellationToken.None);

            Assert.Single(reports);
            Assert.Equal(ReconciliationOutcome.RunOnTerminalAssignment, reports[0].Outcome);
            // Allow a short delay for the kill to propagate to the kernel.
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(dummyProcess.Id);
                    if (p.HasExited) { p.Dispose(); return; }
                    p.Dispose();
                }
                catch (ArgumentException)
                {
                    // Process is gone.
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail("dummy process should have been killed within 1s");
        }
        finally
        {
            try { if (!dummyProcess!.HasExited) dummyProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public async Task Branch4_UncleanShutdownMarkerPresentAndProcessGone_Quarantines()
    {
        var (service, registry, runtime) = Build();
        // Register with a gone pid and the marker present (RegisterAsync always
        // creates the marker).
        var rec = await registry.RegisterAsync(
            MakeRecord("run-1", int.MaxValue, LocalRunState.Running, withUncleanMarker: true),
            CancellationToken.None);

        var reports = await service.ReconcileOnceAsync(CancellationToken.None);

        Assert.Single(reports);
        Assert.Equal(ReconciliationOutcome.UncleanShutdownQuarantined, reports[0].Outcome);
        Assert.NotNull(reports[0].EvidencePath);
    }

    [Fact]
    public async Task Branch4_UncleanShutdownMarkerPresentAndProcessAlive_ReAdoptsAndClearsMarker()
    {
        var (service, registry, _) = Build();
        var rec = await registry.RegisterAsync(
            MakeRecord("run-1", Environment.ProcessId, LocalRunState.Running, withUncleanMarker: true),
            CancellationToken.None);
        var markerPath = Path.Combine(rec.RunDir, RunRegistry.UncleanShutdownMarkerFileName);
        Assert.True(File.Exists(markerPath));

        var reports = await service.ReconcileOnceAsync(CancellationToken.None);

        Assert.Single(reports);
        Assert.Equal(ReconciliationOutcome.ReAdopted, reports[0].Outcome);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ReconcileOnceAsync_NoActiveRuns_EmptyReports()
    {
        var (service, _, _) = Build();
        var reports = await service.ReconcileOnceAsync(CancellationToken.None);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ReconcileOnceAsync_RunFromPreviousLifetime_PicksUpMarkerAndQuarantines()
    {
        // Simulate a previous host lifetime: register a run, then build a fresh
        // registry and reconciliation service. The fresh service should scan
        // from disk, find the marker, and quarantine.
        var (_, registry1, _) = Build();
        await registry1.RegisterAsync(
            MakeRecord("run-orphan", int.MaxValue, LocalRunState.Running),
            CancellationToken.None);

        var (service2, _, _) = Build();
        var reports = await service2.ReconcileOnceAsync(CancellationToken.None);

        Assert.Single(reports);
        Assert.Equal(ReconciliationOutcome.UncleanShutdownQuarantined, reports[0].Outcome);
        Assert.Equal("run-orphan", reports[0].LocalRunId);
    }
}
