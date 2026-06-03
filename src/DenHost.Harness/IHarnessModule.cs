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
/// The firewall boundary between den-host and any concrete harness.
/// The full wake/launch/resume/stop/session/cleanup/evidence interface
/// is defined in den-host task #1917. This minimal shape exists in #1914
/// to establish the assembly-level firewall and the configuration slot
/// without leaking harness-specific concepts into DenHost or Core/Channels.
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
}
