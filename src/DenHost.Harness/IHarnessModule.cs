namespace DenHost.Harness;

/// <summary>
/// Kind of harness module that can plug into den-host.
/// All concrete harnesses (Hermes, Pi-style helper/actor runtime,
/// Codex CLI, Claude Code, OpenCode, future Den-native actor runtime)
/// must be expressed as one of these kinds.
/// </summary>
public enum HarnessModuleKind
{
    /// <summary>
    /// Placeholder kind used by the stub harness module slot
    /// before a real harness is wired in. Not addressable from Core/Channels.
    /// </summary>
    Stub = 0,

    /// <summary>
    /// Hermes messenger/runtime. Owns Hermes-specific profile/env/session paths
    /// entirely within its module assembly. (#1917)
    /// </summary>
    Hermes = 1,

    /// <summary>
    /// Pi-style helper/actor runtime. Future work. (#future)
    /// </summary>
    Pi = 2,

    /// <summary>
    /// Codex CLI harness. Future work. (#future)
    /// </summary>
    Codex = 3,

    /// <summary>
    /// Claude Code harness. Future work. (#future)
    /// </summary>
    ClaudeCode = 4,

    /// <summary>
    /// OpenCode harness. Future work. (#future)
    /// </summary>
    OpenCode = 5,

    /// <summary>
    /// Future Den-native actor runtime. (#future)
    /// </summary>
    DenNative = 99,
}

/// <summary>
/// A capability a harness module can satisfy. Generic Den-facing
/// description: a role name and an optional list of capability tokens.
/// The harness module knows how to map these to its own internal
/// concept (e.g. Hermes profile); den-host does not branch on
/// capability contents in the generic host code.
/// </summary>
public sealed record HarnessCapability(string Role, IReadOnlyList<string> CapabilityTokens);

/// <summary>
/// A pool member that a harness module can address. Generic Den-facing
/// identifier and role; the harness module keeps the runtime-specific
/// addressing data (Hermes profile, Pi/Codex session name, etc.) inside
/// its own assembly.
/// </summary>
public sealed record PoolMemberDescriptor(
    string PoolMemberId,
    string Role,
    string? DisplayName,
    bool Enabled);

/// <summary>
/// Envelope describing a wake decision. Generic Den-facing fields only.
/// The harness firewall guarantees Core/Channels never see Hermes/Pi/Codex/
/// OpenCode/Claude Code internals; the envelope is the same shape regardless
/// of which harness module handles the wake.
/// </summary>
/// <param name="ProjectId">Project the wake targets (e.g. "den-core").</param>
/// <param name="TaskId">Task the wake targets, if any.</param>
/// <param name="AssignmentId">Core assignment id, if any.</param>
/// <param name="RunId">Core worker-run id, if any.</param>
/// <param name="PoolMemberId">Pool member id to address, if known.</param>
/// <param name="Role">Generic role name (e.g. "coder", "reviewer").</param>
/// <param name="SourceContext">Optional source-context metadata for logging.</param>
public sealed record WakeEnvelope(
    string ProjectId,
    int? TaskId,
    int? AssignmentId,
    string? RunId,
    string? PoolMemberId,
    string? Role,
    string? SourceContext);

/// <summary>
/// Handle to a running worker that a harness module has launched.
/// Generic across harness modules: the local run id, the harness
/// kind, the OS process id (if any), when it started, where its log
/// file lives, and the current observed status. The harness module
/// knows how to stop / collect evidence / reset session using this
/// handle; den-host does not look at its internals.
/// </summary>
public sealed record WorkerHandle(
    string LocalRunId,
    HarnessModuleKind Kind,
    int? ProcessId,
    DateTimeOffset StartedAt,
    string LogFilePath,
    WorkerStatus Status);

public enum WorkerStatus
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopped = 3,
    Failed = 4,
}

/// <summary>
/// Evidence collected for a worker run. Generic Den-facing fields only.
/// The harness module decides how to gather this; den-host does not look
/// at harness-internal evidence formats.
/// </summary>
/// <param name="LocalRunId">Local run id matching <see cref="WorkerHandle.LocalRunId"/>.</param>
/// <param name="ExitCode">Process exit code, if the run terminated.</param>
/// <param name="EndedAt">When the run ended, if known.</param>
/// <param name="LogTail">Last N bytes of the run log, if requested.</param>
/// <param name="Notes">Module-specific notes that are still Den-facing (e.g. "hermes exited 0 after 1 turn").</param>
public sealed record HarnessRunEvidence(
    string LocalRunId,
    int? ExitCode,
    DateTimeOffset? EndedAt,
    string? LogTail,
    string? Notes);

/// <summary>
/// The firewall boundary between den-host and any concrete harness.
/// The full wake/launch/resume/stop/session/cleanup/evidence interface
/// covers the points required by den-host task #1917. Default
/// implementations on the interface keep the stub and other minimal
/// implementations small.
/// </summary>
public interface IHarnessModule
{
    /// <summary>
    /// Stable name used in config and logging. Must be unique within a host.
    /// Convention: lowercase, hyphen-separated, e.g. "hermes-default".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Kind of harness. DenHost uses this for diagnostics only;
    /// it does not branch behavior on Kind in the generic host code.
    /// </summary>
    HarnessModuleKind Kind { get; }

    /// <summary>
    /// Returns true if the harness can be reached / launched on this host
    /// given the host's current configuration and local prerequisites.
    /// Implementations must be cheap and side-effect-free; they must not
    /// spawn processes or open network connections.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Returns the generic Den-facing capabilities this module can satisfy.
    /// Default: none. Real modules override.
    /// </summary>
    Task<IReadOnlyList<HarnessCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HarnessCapability>>(Array.Empty<HarnessCapability>());

    /// <summary>
    /// Enumerates the locally-known pool members this module can address.
    /// Default: none. Real modules override.
    /// </summary>
    Task<IReadOnlyList<PoolMemberDescriptor>> GetLocalInventoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PoolMemberDescriptor>>(Array.Empty<PoolMemberDescriptor>());

    /// <summary>
    /// Wakes a worker from the envelope. Returns a handle the host can
    /// later use to stop, collect evidence, or reset. Default: throws
    /// <see cref="NotSupportedException"/>. Real modules override.
    /// </summary>
    Task<WorkerHandle> WakeAsync(WakeEnvelope envelope, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Harness module '{Name}' does not implement WakeAsync.");

    /// <summary>
    /// Stops a running worker. Default: throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task StopAsync(WorkerHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Harness module '{Name}' does not implement StopAsync.");

    /// <summary>
    /// Collects run evidence for a completed or running worker.
    /// Default: returns a minimal evidence record with no log tail.
    /// </summary>
    Task<HarnessRunEvidence> CollectEvidenceAsync(WorkerHandle handle, CancellationToken cancellationToken) =>
        Task.FromResult(new HarnessRunEvidence(handle.LocalRunId, null, null, null,
            $"{handle.Kind} module '{Name}' did not provide run evidence."));

    /// <summary>
    /// Resets the harness session state for a worker (e.g. on
    /// quarantine). Default: no-op. Real modules override.
    /// </summary>
    Task ResetSessionAsync(WorkerHandle handle, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Runs a bounded, side-effect-light smoke that verifies the module
    /// can launch its underlying harness on this host. Used by
    /// <c>den-host smoke &lt;module-name&gt;</c> and by reconciliation
    /// diagnostics. Default: returns a smoke that did not run.
    /// </summary>
    Task<HarnessSmokeResult> SmokeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HarnessSmokeResult(
            ModuleName: Name,
            Kind: Kind,
            Outcome: HarnessSmokeOutcome.NotImplemented,
            Detail: $"Harness module '{Name}' does not implement SmokeAsync."));
}

/// <summary>
/// Outcome of a harness module smoke test.
/// </summary>
public enum HarnessSmokeOutcome
{
    /// <summary>Smoke test is not implemented for this module kind.</summary>
    NotImplemented = 0,
    /// <summary>Smoke ran successfully (e.g. hermes --version returned 0).</summary>
    Passed = 1,
    /// <summary>Smoke ran but the harness reported a problem (non-zero exit).</summary>
    Failed = 2,
    /// <summary>Smoke could not be run because local prerequisites are missing.</summary>
    Blocked = 3,
}

/// <summary>
/// Result of a single harness module smoke.
/// </summary>
/// <param name="ModuleName">The harness module's name.</param>
/// <param name="Kind">The harness kind.</param>
/// <param name="Outcome">Smoke outcome.</param>
/// <param name="Detail">Free-text diagnostic detail (e.g. hermes version output or blocker reason).</param>
/// <param name="BlockerEvidencePath">Path to a blocker-evidence file on disk, when <paramref name="Outcome"/> is <see cref="HarnessSmokeOutcome.Blocked"/>.</param>
public sealed record HarnessSmokeResult(
    string ModuleName,
    HarnessModuleKind Kind,
    HarnessSmokeOutcome Outcome,
    string? Detail,
    string? BlockerEvidencePath = null);
