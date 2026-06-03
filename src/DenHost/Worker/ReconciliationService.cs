using System.Diagnostics;
using System.Text.Json;
using DenHost.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DenHost.Worker;

/// <summary>
/// Reconciles local run state with Core assignment state. Runs at
/// host startup (mandatory) and periodically (best-effort). Writes
/// quarantine evidence under <c>RuntimeOptions.QuarantineDir</c> when
/// the local state cannot be trusted. Per den-host task #1918, the
/// host does NOT decide canonical assignment completion locally; it
/// only reports evidence to Core. The Core contract task that defines
/// the receive endpoint is still pending; until it lands, the host
/// writes a structured evidence file and the service is
/// "evidence-only" rather than reporting inline.
/// </summary>
public sealed class ReconciliationService : BackgroundService, IReconciliationService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RunRegistry _registry;
    private readonly RuntimeOptions _runtime;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        RunRegistry registry,
        RuntimeOptions runtime,
        ILogger<ReconciliationService> logger)
    {
        _registry = registry;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reconciliation: starting initial pass at host startup");
        try
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial reconciliation pass threw; will continue to next pass");
        }

        // Periodic passes are best-effort. CancellationToken is honored.
        var interval = TimeSpan.FromSeconds(Math.Max(10, _runtime.BindingHeartbeatSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Periodic reconciliation pass threw; will retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Perform a single reconciliation pass over the registry. Returns
    /// the per-run reports. Exposed so <c>den-host reconcile</c> can
    /// run a one-shot pass without going through the background loop.
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationReport>> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        // Repopulate the registry from disk first so we observe runs
        // started by a previous host lifetime.
        await _registry.ScanFromDiskAsync(cancellationToken).ConfigureAwait(false);

        var reports = new List<ReconciliationReport>();
        foreach (var record in _registry.Active().ToList())
        {
            var report = await ReconcileOneAsync(record, cancellationToken).ConfigureAwait(false);
            reports.Add(report);
        }
        return reports;
    }

    private async Task<ReconciliationReport> ReconcileOneAsync(LocalRunRecord record, CancellationToken cancellationToken)
    {
        var processObserved = DescribeProcess(record);
        var assignmentStateObserved = DescribeAssignmentLocal(record);
        var processAlive = record.ProcessId is int ppid && IsProcessAlive(ppid);
        var markerPath = Path.Combine(record.RunDir, RunRegistry.UncleanShutdownMarkerFileName);
        var markerPresent = File.Exists(markerPath);

        // Branch 3: process exists + terminal assignment -- KILL first, because
        // we do not want a live harness to keep running against a terminal
        // assignment even if the marker is present.
        if (processAlive && assignmentStateObserved == "terminal")
        {
            TryKillProcess(record.ProcessId!.Value);
            TryDelete(markerPath);
            var note = "Process was alive locally but assignment is terminal; process terminated.";
            return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.RunOnTerminalAssignment, processObserved, assignmentStateObserved, note, null);
        }

        // Branch 1: running process + running assignment -- re-adopt and clear
        // any leftover marker from a previous lifetime.
        if (processAlive && assignmentStateObserved == "active")
        {
            if (markerPresent) TryDelete(markerPath);
            return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.ReAdopted, processObserved, assignmentStateObserved, "Live process + active assignment; re-adopted.", null);
        }

        // Branch 2: missing process + running assignment (and no unclean marker).
        if (!processAlive && assignmentStateObserved == "active" && !markerPresent)
        {
            var note = "Process is gone locally but assignment is still active; evidence written; Core to be notified (Core contract pending).";
            var evidence = await WriteEvidenceAsync(record, ReconciliationOutcome.StaleAssignmentActive, processObserved, assignmentStateObserved, note, cancellationToken).ConfigureAwait(false);
            var stale = record with { State = LocalRunState.Mismatch, ProcessId = null };
            await _registry.UpdateStateAsync(stale, cancellationToken).ConfigureAwait(false);
            return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.StaleAssignmentActive, processObserved, assignmentStateObserved, note, evidence);
        }

        // Branch 4: unclean shutdown marker present and the process is gone --
        // quarantine. If the process were alive, branch 1 would have fired.
        if (markerPresent && !processAlive)
        {
            var note = "Unclean shutdown marker was present and process is gone; quarantining.";
            var evidence = await WriteEvidenceAsync(record, ReconciliationOutcome.UncleanShutdownQuarantined, processObserved, assignmentStateObserved, note, cancellationToken).ConfigureAwait(false);
            var quarantined = record with { State = LocalRunState.Quarantined, ProcessId = null };
            await _registry.UpdateStateAsync(quarantined, cancellationToken).ConfigureAwait(false);
            return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.UncleanShutdownQuarantined, processObserved, assignmentStateObserved, note, evidence);
        }

        // Branch 5: Core unreachable; hold the run in place rather than deciding
        // locally. (Local-only reconciliation reports this when the local state
        // looks like an earlier-incomplete pass.)
        if (assignmentStateObserved == "unreachable" && processAlive)
        {
            return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.HeldBecauseCoreUnreachable, processObserved, assignmentStateObserved, "Core unreachable; held run in place rather than deciding locally.", null);
        }

        // Otherwise: cleanly closed.
        return new ReconciliationReport(record.LocalRunId, record.AssignmentId, ReconciliationOutcome.CleanlyClosed, processObserved, assignmentStateObserved, "No action; run considered clean.", null);
    }

    private static string DescribeProcess(LocalRunRecord r)
    {
        if (r.ProcessId is int pid)
        {
            return IsProcessAlive(pid) ? $"alive pid={pid}" : "gone";
        }
        return "never";
    }

    private static string DescribeAssignmentLocal(LocalRunRecord r)
    {
        // The local-only reconciliation in #1918 does not query Core. It
        // uses the local unclean-shutdown marker + process state to
        // produce a best-effort "active" / "terminal" / "unreachable" label.
        // The actual Core reporting is blocked on the Core contract task.
        if (r.State == LocalRunState.Starting || r.State == LocalRunState.Running) return "active";
        if (r.State == LocalRunState.Stopped) return "terminal";
        if (r.State == LocalRunState.Quarantined) return "terminal";
        if (r.State == LocalRunState.UncleanShutdown) return "unreachable";
        if (r.State == LocalRunState.Mismatch) return "terminal";
        return "active";
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var alive = !p.HasExited;
            p.Dispose();
            return alive;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKillProcess(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private async Task<string> WriteEvidenceAsync(
        LocalRunRecord record,
        ReconciliationOutcome outcome,
        string processObserved,
        string assignmentStateObserved,
        string note,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runtime.QuarantineDir);
        var fileName = $"reconciliation-{record.LocalRunId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{outcome.ToString().ToLowerInvariant()}.json";
        var path = Path.Combine(_runtime.QuarantineDir, fileName);
        var dto = new
        {
            localRunId = record.LocalRunId,
            assignmentId = record.AssignmentId,
            harnessKind = record.HarnessKind.ToString(),
            harnessModuleName = record.HarnessModuleName,
            outcome = outcome.ToString().ToLowerInvariant(),
            processObserved,
            assignmentStateObserved,
            note,
            runDir = record.RunDir,
            logFilePath = record.LogFilePath,
            detectedAt = DateTimeOffset.UtcNow,
            hostInstanceId = record.HarnessModuleName,
        };
        var json = JsonSerializer.Serialize(dto, s_jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
