using System.ComponentModel.DataAnnotations;

namespace DenHost.Configuration;

/// <summary>
/// Identity of this den-host instance as seen by Core/Channels.
/// This is a Den-facing concept. It is intentionally generic:
/// it does not leak Hermes/Pi/Codex/Claude Code/OpenCode internals.
/// </summary>
public sealed class AdapterOptions
{
    public const string SectionName = "Adapter";

    /// <summary>
    /// Logical kind of this adapter, e.g. "host". The set of allowed
    /// values is intentionally open for now; Core/Channels should treat
    /// it as an opaque label.
    /// </summary>
    [Required]
    public string Kind { get; init; } = "host";

    /// <summary>
    /// Stable, globally-unique-per-host identifier for this adapter instance.
    /// Should be persisted across restarts. Used in Core/Channels bindings
    /// and in any worker-run source/target context.
    /// </summary>
    [Required]
    public string InstanceId { get; init; } = "";

    /// <summary>
    /// Human-readable machine identifier (e.g. "workstation-01", "laptop-a").
    /// Not used as a primary key anywhere; just an operator-friendly label.
    /// </summary>
    [Required]
    public string Host { get; init; } = "";

    /// <summary>
    /// Roles this host claims to be able to satisfy via its harness modules.
    /// These are Den-facing role names, not harness-specific profile names.
    /// </summary>
    public IReadOnlyList<string> ManagedRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Generic capabilities this host claims to be able to satisfy.
    /// These are Den-facing capability tokens (e.g. "worker.coder",
    /// "worker.reviewer"), not harness-specific concepts.
    /// </summary>
    public IReadOnlyList<string> ManagedCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional project id scope for the adapter binding. When set, the
    /// binding is scoped to this project. Defaults to null (unscoped).
    /// </summary>
    public string? ProjectId { get; init; }
}
