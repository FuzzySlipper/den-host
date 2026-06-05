using DenHost.Configuration;
using DenHost.Harness;
using DenHost.Host;
using DenHost.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class RunRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public RunRegistryTests()
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

    private (RunRegistry Registry, RuntimeOptions Runtime, AdapterIdentity Identity) Build()
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
        return (registry, runtime, identity);
    }

    [Fact]
    public async Task RegisterAsync_CreatesRunDirWithStateAndMarker()
    {
        var (registry, _, _) = Build();
        var record = new LocalRunRecord(
            LocalRunId: "run-1",
            WorkerRunId: null,
            AssignmentId: 100,
            TaskId: null,
            Role: null,
            ProfileIdentity: null,
            PoolMemberId: null,
            HarnessKind: HarnessModuleKind.Hermes,
            HarnessModuleName: "hermes-default",
            ProcessId: Environment.ProcessId,
            LogFilePath: "/tmp/log.log",
            StartedAt: DateTimeOffset.UtcNow,
            State: LocalRunState.Starting,
            RunDir: "");

        var saved = await registry.RegisterAsync(record, CancellationToken.None);

        Assert.True(Directory.Exists(saved.RunDir));
        Assert.True(File.Exists(Path.Combine(saved.RunDir, RunRegistry.RunStateFileName)));
        Assert.True(File.Exists(Path.Combine(saved.RunDir, RunRegistry.PidFileName)));
        Assert.True(File.Exists(Path.Combine(saved.RunDir, RunRegistry.LogPointerFileName)));
        Assert.True(File.Exists(Path.Combine(saved.RunDir, RunRegistry.UncleanShutdownMarkerFileName)));
    }

    [Fact]
    public async Task RegisterAsync_AddsToActiveList()
    {
        var (registry, _, _) = Build();
        var record = new LocalRunRecord("run-1", null, 100, null, null, null, null, HarnessModuleKind.Hermes, "h", null, "/tmp/log", DateTimeOffset.UtcNow, LocalRunState.Starting, "");
        await registry.RegisterAsync(record, CancellationToken.None);
        Assert.True(registry.TryGet("run-1", out var active));
        Assert.Equal("run-1", active.LocalRunId);
    }

    [Fact]
    public async Task MarkCleanlyStoppedAsync_RemovesMarkerAndFromActive()
    {
        var (registry, _, _) = Build();
        var record = new LocalRunRecord("run-1", null, 100, null, null, null, null, HarnessModuleKind.Hermes, "h", Environment.ProcessId, "/tmp/log", DateTimeOffset.UtcNow, LocalRunState.Starting, "");
        var saved = await registry.RegisterAsync(record, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(saved.RunDir, RunRegistry.UncleanShutdownMarkerFileName)));

        var ok = await registry.MarkCleanlyStoppedAsync("run-1", CancellationToken.None);

        Assert.True(ok);
        Assert.False(registry.TryGet("run-1", out _));
        Assert.False(File.Exists(Path.Combine(saved.RunDir, RunRegistry.UncleanShutdownMarkerFileName)));
    }

    [Fact]
    public async Task ScanFromDiskAsync_RepopulatesActive()
    {
        var (registry, _, _) = Build();
        var record = new LocalRunRecord("run-1", null, 100, null, null, null, null, HarnessModuleKind.Hermes, "h", null, "/tmp/log", DateTimeOffset.UtcNow, LocalRunState.Starting, "");
        var saved = await registry.RegisterAsync(record, CancellationToken.None);

        var (registry2, _, _) = Build();
        var scanned = await registry2.ScanFromDiskAsync(CancellationToken.None);

        Assert.Single(scanned);
        Assert.Equal("run-1", scanned[0].LocalRunId);
        Assert.Equal(saved.RunDir, scanned[0].RunDir);
        Assert.Equal(LocalRunState.Starting, scanned[0].State);
    }

    [Fact]
    public async Task ScanFromDiskAsync_MissingRunDir_ReturnsEmpty()
    {
        var (registry, _, _) = Build();
        var scanned = await registry.ScanFromDiskAsync(CancellationToken.None);
        Assert.Empty(scanned);
    }

    [Fact]
    public async Task ScanFromDiskAsync_DoesNotOverrideStateFromMarker()
    {
        // The on-disk state.json is authoritative; the marker file is
        // inspected separately by the reconciliation service.
        var (registry, _, _) = Build();
        var record = new LocalRunRecord("run-1", null, 100, null, null, null, null, HarnessModuleKind.Hermes, "h", null, "/tmp/log", DateTimeOffset.UtcNow, LocalRunState.Running, "");
        await registry.RegisterAsync(record, CancellationToken.None);

        var (registry2, _, _) = Build();
        var scanned = await registry2.ScanFromDiskAsync(CancellationToken.None);

        Assert.Single(scanned);
        Assert.Equal(LocalRunState.Running, scanned[0].State);
    }
}
