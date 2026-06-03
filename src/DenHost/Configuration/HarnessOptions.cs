using System.ComponentModel.DataAnnotations;
using DenHost.Harness;

namespace DenHost.Configuration;

/// <summary>
/// Configuration for a single harness module slot. The Settings
/// dictionary is harness-specific opaque data that the module
/// implementation itself parses. DenHost does not interpret
/// Settings contents.
/// </summary>
public sealed class HarnessModuleConfig
{
    /// <summary>
    /// Stable name for this module, unique within the host.
    /// Used in logs and in any Den-visible status.
    /// </summary>
    [Required]
    public string Name { get; init; } = "";

    /// <summary>
    /// Kind of harness module. The DenHost only validates the value
    /// and uses it for diagnostics; it does not branch behavior on
    /// Kind in the generic host code.
    /// </summary>
    [Required]
    public HarnessModuleKind Kind { get; init; } = HarnessModuleKind.Stub;

    /// <summary>
    /// Whether this module is enabled. Disabled modules are not
    /// loaded; their IsAvailable() is never consulted.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Harness-specific opaque settings. Parsed by the module
    /// implementation; DenHost does not look at this dictionary.
    /// For example, a Hermes module would read its profile name,
    /// binary path, env overrides, etc. from here.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Root shape for harness module configuration. DenHost exposes
/// the list of configured modules and lets the harness interface
/// factory resolve each into an IHarnessModule implementation.
/// </summary>
public sealed class HarnessOptions
{
    public const string SectionName = "Harness";

    /// <summary>
    /// Module configurations. Order is preserved. The first
    /// enabled module per (name) wins if duplicates exist.
    /// </summary>
    [Required]
    public IReadOnlyList<HarnessModuleConfig> Modules { get; init; }
        = Array.Empty<HarnessModuleConfig>();
}
