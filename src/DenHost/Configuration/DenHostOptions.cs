using System.ComponentModel.DataAnnotations;

namespace DenHost.Configuration;

/// <summary>
/// Root options for den-host. Bound from a single den-host.json file
/// (or a path passed via --config). All sections are validated on
/// startup; the host refuses to run if any required field is missing.
/// </summary>
public sealed class DenHostOptions
{
    public const string SectionName = "DenHost";

    [Required]
    public AdapterOptions Adapter { get; init; } = new();

    [Required]
    public CoreOptions Core { get; init; } = new();

    [Required]
    public ChannelsOptions Channels { get; init; } = new();

    [Required]
    public RuntimeOptions Runtime { get; init; } = new();

    [Required]
    public HarnessOptions Harness { get; init; } = new();
}
