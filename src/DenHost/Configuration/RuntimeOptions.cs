using System.ComponentModel.DataAnnotations;

namespace DenHost.Configuration;

/// <summary>
/// Local filesystem layout for den-host. All paths are local to the
/// machine and are the only durable state den-host owns. Core/Channels
/// remain the source of truth for assignments, runs, completions,
/// and quarantine decisions.
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    /// <summary>
    /// Directory containing the active den-host.json. Usually the
    /// den-host repo root or a deployment config dir.
    /// </summary>
    [Required]
    public string ConfigDir { get; init; } = "";

    /// <summary>
    /// Directory under which per-run subdirectories are created,
    /// e.g. .runtime/run/&lt;assignment_id&gt;/&lt;run_id&gt;/.
    /// This is the canonical location for PID files, log pointers,
    /// and per-run evidence.
    /// </summary>
    [Required]
    public string RunDir { get; init; } = ".runtime/run";

    /// <summary>
    /// Directory for non-run-scoped local state, e.g. shadow-mode
    /// event cursors, last-known binding timestamps, marker files.
    /// </summary>
    [Required]
    public string StateDir { get; init; } = ".runtime/state";

    /// <summary>
    /// Directory for harness module logs and shared host logs.
    /// </summary>
    [Required]
    public string LogDir { get; init; } = ".runtime/log";

    /// <summary>
    /// Directory for quarantine evidence files (den-host task #1918).
    /// Files in this directory are Core-visible records of local
    /// reconciliation outcomes; they are not themselves Core state.
    /// </summary>
    [Required]
    public string QuarantineDir { get; init; } = ".runtime/quarantine";

    /// <summary>
    /// Interval in seconds between adapter binding heartbeats to Core.
    /// The first probe runs at host startup; subsequent probes run on
    /// this interval. Set to 0 to disable the heartbeat (one-shot
    /// <c>den-host binding</c> is still available).
    /// </summary>
    [Range(0, 86_400)]
    public int BindingHeartbeatSeconds { get; init; } = 30;

    /// <summary>
    /// Interval in seconds between legacy Channels direct-agent event reads
    /// when running in explicit cold-history/shadow mode. Defaults to 0
    /// because den-host is not an active worker wake reader.
    /// </summary>
    [Range(0, 86_400)]
    public int ChannelsEventPollSeconds { get; init; } = 0;

    /// <summary>
    /// Page size for the Channels direct-agent event reader.
    /// </summary>
    [Range(1, 1_000)]
    public int ChannelsEventPageSize { get; init; } = 50;
}
