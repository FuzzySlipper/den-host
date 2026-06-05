using System.Text.Json;
using DenHost.Configuration;
using DenHost.Harness;
using DenHost.Host;
using Microsoft.Extensions.Logging;

namespace DenHost.Worker;

/// <summary>
/// Local registry of worker runs. The registry is the host's
/// authoritative local view; Core remains the canonical source of
/// truth for assignment completion. The registry persists each
/// registered run to a per-run directory under
/// <c>RuntimeOptions.RunDir/&lt;assignment&gt;/&lt;local-run-id&gt;</c>.
///
/// On startup, the <see cref="ReconciliationService"/> calls
/// <see cref="ScanFromDiskAsync"/> to repopulate the registry from
/// the on-disk state.
/// </summary>
public sealed class RunRegistry
{
    public const string UncleanShutdownMarkerFileName = ".unclean-shutdown";
    public const string RunStateFileName = "state.json";
    public const string EnvelopeFileName = "envelope.json";
    public const string PidFileName = "pid";
    public const string LogPointerFileName = "log";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RuntimeOptions _runtime;
    private readonly AdapterIdentity _identity;
    private readonly ILogger<RunRegistry> _logger;
    private readonly Dictionary<string, LocalRunRecord> _byId = new(StringComparer.Ordinal);

    public RunRegistry(RuntimeOptions runtime, AdapterIdentity identity, ILogger<RunRegistry> logger)
    {
        _runtime = runtime;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>
    /// Directory under which per-run subdirectories are created.
    /// <c>{RunDir}/{adapter-instance-id}/</c>.
    /// </summary>
    public string HostRunRoot => Path.Combine(_runtime.RunDir, SafePathSegment(_identity.InstanceId));

    /// <summary>
    /// Register a new run, creating its on-disk metadata.
    /// </summary>
    public async Task<LocalRunRecord> RegisterAsync(LocalRunRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var runDir = EnsureRunDir(record);
        await WriteStateAsync(record with { RunDir = runDir }, cancellationToken).ConfigureAwait(false);
        if (record.ProcessId is int pid)
        {
            await File.WriteAllTextAsync(Path.Combine(runDir, PidFileName), pid.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(record.LogFilePath))
        {
            await File.WriteAllTextAsync(Path.Combine(runDir, LogPointerFileName), record.LogFilePath, cancellationToken).ConfigureAwait(false);
        }
        // Mark the host as not yet cleanly shut down.
        await File.WriteAllTextAsync(
            Path.Combine(runDir, UncleanShutdownMarkerFileName),
            $"started at {_identity.InstanceId} {DateTimeOffset.UtcNow:O}",
            cancellationToken).ConfigureAwait(false);
        _byId[record.LocalRunId] = record with { RunDir = runDir };
        return _byId[record.LocalRunId];
    }

    /// <summary>
    /// Mark a run as cleanly stopped: remove the unclean-shutdown marker,
    /// update the state file, and remove the run from the in-memory map.
    /// </summary>
    public async Task<bool> MarkCleanlyStoppedAsync(string localRunId, CancellationToken cancellationToken)
    {
        if (!_byId.TryGetValue(localRunId, out var record))
        {
            return false;
        }
        var marker = Path.Combine(record.RunDir, UncleanShutdownMarkerFileName);
        TryDelete(marker);
        var updated = record with { State = LocalRunState.Stopped, ProcessId = null };
        await WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
        _byId.Remove(localRunId);
        return true;
    }

    /// <summary>
    /// Replace the state of a run on disk (e.g. after reconciliation
    /// marks it Quarantined) without removing it from the in-memory map.
    /// </summary>
    public async Task UpdateStateAsync(LocalRunRecord record, CancellationToken cancellationToken)
    {
        await WriteStateAsync(record, cancellationToken).ConfigureAwait(false);
        _byId[record.LocalRunId] = record;
    }

    public bool TryGet(string localRunId, out LocalRunRecord record) =>
        _byId.TryGetValue(localRunId, out record!);

    public IEnumerable<LocalRunRecord> Active() => _byId.Values;

    /// <summary>
    /// Scan <see cref="HostRunRoot"/> and repopulate the in-memory
    /// registry from on-disk state. Returns the scanned records.
    /// </summary>
    public Task<IReadOnlyList<LocalRunRecord>> ScanFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(HostRunRoot))
        {
            return Task.FromResult<IReadOnlyList<LocalRunRecord>>(Array.Empty<LocalRunRecord>());
        }

        var results = new List<LocalRunRecord>();
        foreach (var assignmentDir in Directory.EnumerateDirectories(HostRunRoot))
        {
            foreach (var runDir in Directory.EnumerateDirectories(assignmentDir))
            {
                try
                {
                    var record = ReadStateFile(runDir);
                    if (record is null) continue;
                    // The state from the on-disk file is authoritative; the
                    // ReconciliationService inspects the marker file separately
                    // and decides which branch (re-adopt / quarantine / etc.) applies.
                    results.Add(record);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to scan run dir {RunDir}", runDir);
                }
            }
        }
        foreach (var r in results) _byId[r.LocalRunId] = r;
        return Task.FromResult<IReadOnlyList<LocalRunRecord>>(results);
    }

    private string EnsureRunDir(LocalRunRecord record)
    {
        var assignmentSeg = record.AssignmentId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unassigned";
        var runDir = Path.Combine(HostRunRoot, assignmentSeg, SafePathSegment(record.LocalRunId));
        Directory.CreateDirectory(runDir);
        return runDir;
    }

    private LocalRunRecord? ReadStateFile(string runDir)
    {
        var path = Path.Combine(runDir, RunStateFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LocalRunRecordDto>(json, s_jsonOptions)?.ToRecord(runDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read run state from {Path}", path);
            return null;
        }
    }

    private async Task WriteStateAsync(LocalRunRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(record.RunDir);
        var dto = LocalRunRecordDto.From(record);
        var json = JsonSerializer.Serialize(dto, s_jsonOptions);
        await File.WriteAllTextAsync(Path.Combine(record.RunDir, RunStateFileName), json, cancellationToken).ConfigureAwait(false);
    }

    private static string SafePathSegment(string s) => s.Replace('/', '_').Replace('\\', '_');

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private sealed class LocalRunRecordDto
    {
        public string LocalRunId { get; set; } = "";
        public string? WorkerRunId { get; set; }
        public int? AssignmentId { get; set; }
        public int? TaskId { get; set; }
        public string? Role { get; set; }
        public string? ProfileIdentity { get; set; }
        public string? PoolMemberId { get; set; }
        public string HarnessKind { get; set; } = "Stub";
        public string HarnessModuleName { get; set; } = "";
        public int? ProcessId { get; set; }
        public string LogFilePath { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public string State { get; set; } = "Unknown";

        public static LocalRunRecordDto From(LocalRunRecord r) => new()
        {
            LocalRunId = r.LocalRunId,
            WorkerRunId = r.WorkerRunId,
            AssignmentId = r.AssignmentId,
            TaskId = r.TaskId,
            Role = r.Role,
            ProfileIdentity = r.ProfileIdentity,
            PoolMemberId = r.PoolMemberId,
            HarnessKind = r.HarnessKind.ToString(),
            HarnessModuleName = r.HarnessModuleName,
            ProcessId = r.ProcessId,
            LogFilePath = r.LogFilePath,
            StartedAt = r.StartedAt,
            State = r.State.ToString(),
        };

        public LocalRunRecord ToRecord(string runDir) => new(
            LocalRunId: LocalRunId,
            WorkerRunId: WorkerRunId,
            AssignmentId: AssignmentId,
            TaskId: TaskId,
            Role: Role,
            ProfileIdentity: ProfileIdentity,
            PoolMemberId: PoolMemberId,
            HarnessKind: Enum.TryParse<HarnessModuleKind>(HarnessKind, out var hk) ? hk : HarnessModuleKind.Stub,
            HarnessModuleName: HarnessModuleName,
            ProcessId: ProcessId,
            LogFilePath: LogFilePath,
            StartedAt: StartedAt,
            State: Enum.TryParse<LocalRunState>(State, out var s) ? s : LocalRunState.Unknown,
            RunDir: runDir);
    }
}
