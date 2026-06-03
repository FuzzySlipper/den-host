using DenHost.Harness;

namespace DenHost.Worker;

/// <summary>
/// State of a single local worker run, as observed by den-host.
/// The host does NOT derive canonical completion from this state;
/// Core remains the source of truth for assignment/run completion.
/// </summary>
public enum LocalRunState
{
    /// <summary>Run directory was found but no state.json is present yet.</summary>
    Unknown = 0,
    /// <summary>Run is being set up; process is not yet observed alive.</summary>
    Starting = 1,
    /// <summary>Process is alive; the host has re-adopted the run.</summary>
    Running = 2,
    /// <summary>Process is gone; the run was either cleanly stopped or crashed.</summary>
    Stopped = 3,
    /// <summary>Process is alive but the host does not trust the run; evidence written.</summary>
    Quarantined = 4,
    /// <summary>Unclean shutdown marker was present at startup; run was quarantined.</summary>
    UncleanShutdown = 5,
    /// <summary>Local state conflicts with the expected assignment; evidence written.</summary>
    Mismatch = 6,
}

/// <summary>
/// One local worker run. The host keeps this in memory (via
/// <c>RunRegistry</c>) and on disk (under
/// <c>RuntimeOptions.RunDir/&lt;assignment&gt;/&lt;local-run&gt;</c>).
/// </summary>
/// <param name="LocalRunId">Local run id (matches WorkerHandle.LocalRunId).</param>
/// <param name="AssignmentId">Core assignment id, if known at registration time.</param>
/// <param name="HarnessKind">Kind of harness module that owns the run.</param>
/// <param name="HarnessModuleName">Name of the harness module that owns the run.</param>
/// <param name="ProcessId">OS process id, if the run has a live process.</param>
/// <param name="LogFilePath">Path to the run's log file.</param>
/// <param name="StartedAt">When the run was registered.</param>
/// <param name="State">Current local state.</param>
/// <param name="RunDir">Path to the run's directory.</param>
public sealed record LocalRunRecord(
    string LocalRunId,
    int? AssignmentId,
    HarnessModuleKind HarnessKind,
    string HarnessModuleName,
    int? ProcessId,
    string LogFilePath,
    DateTimeOffset StartedAt,
    LocalRunState State,
    string RunDir);

/// <summary>
/// Outcome of a single reconciliation branch.
/// </summary>
public enum ReconciliationOutcome
{
    /// <summary>Run is alive locally and assignment is active in Core; host re-adopted.</summary>
    ReAdopted = 0,
    /// <summary>Run is gone locally but assignment is still active in Core; evidence written.</summary>
    StaleAssignmentActive = 1,
    /// <summary>Run is alive locally but assignment is terminal in Core; host stopped the process.</summary>
    RunOnTerminalAssignment = 2,
    /// <summary>Unclean shutdown marker was present; run quarantined.</summary>
    UncleanShutdownQuarantined = 3,
    /// <summary>Local evidence conflicts with Core; quarantine evidence written.</summary>
    MismatchQuarantined = 4,
    /// <summary>Run is alive locally; Core is unreachable; host held the run in place.</summary>
    HeldBecauseCoreUnreachable = 5,
    /// <summary>Run is gone locally and assignment is terminal; cleanly closed.</summary>
    CleanlyClosed = 6,
}

/// <summary>
/// One reconciliation report. The reconciliation service produces
/// one of these per pass; each is logged and (if appropriate)
/// persisted to <c>RuntimeOptions.QuarantineDir</c> as Core-visible
/// evidence.
/// </summary>
/// <param name="LocalRunId">Local run id.</param>
/// <param name="AssignmentId">Core assignment id, if known.</param>
/// <param name="Outcome">The branch the host took.</param>
/// <param name="ProcessObserved">What the host saw locally for the process (alive / gone / never).</param>
/// <param name="AssignmentStateObserved">What the host learned from Core (active / terminal / unreachable / unknown).</param>
/// <param name="Note">Free-text diagnostic detail.</param>
/// <param name="EvidencePath">Path to the evidence file on disk, if one was written.</param>
public sealed record ReconciliationReport(
    string LocalRunId,
    int? AssignmentId,
    ReconciliationOutcome Outcome,
    string ProcessObserved,
    string AssignmentStateObserved,
    string? Note,
    string? EvidencePath);
