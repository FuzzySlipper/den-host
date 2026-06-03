using System.ComponentModel.DataAnnotations;

namespace DenHost.Configuration;

/// <summary>
/// Base shape for a Den endpoint (Core or Channels). Concrete
/// subclasses bind to "Core" or "Channels" sections of the
/// den-host.json config and provide type-safe accessors.
/// </summary>
public abstract class EndpointOptions
{
    /// <summary>
    /// Absolute base URL of the endpoint, e.g. "http://127.0.0.1:18081".
    /// Must be an absolute URI. Loopback is preferred for direct delivery
    /// so den-host does not require ordinary agents to know LAN topology.
    /// </summary>
    [Required]
    public string BaseUrl { get; init; } = "";

    /// <summary>
    /// Path used for the reachability probe. Defaults to /healthz.
    /// </summary>
    public string HealthPath { get; init; } = "/healthz";

    /// <summary>
    /// Optional path used for adapter binding/heartbeat, if Core exposes
    /// one. Defaults to null (no binding endpoint configured).
    /// </summary>
    public string? BindingPath { get; init; }

    /// <summary>
    /// Per-request timeout in milliseconds for HTTP calls to this endpoint.
    /// </summary>
    [Range(100, 600_000)]
    public int TimeoutMs { get; init; } = 5_000;

    /// <summary>
    /// Optional API key / bearer token. Prefer sourcing from the
    /// DEN_HOST_CORE_API_KEY / DEN_HOST_CHANNELS_API_KEY environment
    /// variables rather than committing to den-host.json.
    /// </summary>
    public string? ApiKey { get; init; }
}

/// <summary>
/// Configuration for the Core endpoint. Core owns canonical
/// workflow truth (tasks, assignments, leases, runs, pool members,
/// adapter binding projections, completion/block/failure, release/quarantine).
/// </summary>
public sealed class CoreOptions : EndpointOptions
{
    public const string SectionName = "Core";
}

/// <summary>
/// Configuration for the Channels endpoint. Channels owns the
/// operations conversation (rooms, messages, activity, direct-agent
/// event creation, source context, target work metadata, delivery/
/// checkpoint traces).
/// </summary>
public sealed class ChannelsOptions : EndpointOptions
{
    public const string SectionName = "Channels";

    /// <summary>
    /// Optional path for the direct-agent event stream. Defaults to
    /// /api/direct-agent/events.
    /// </summary>
    public string DirectAgentEventsPath { get; init; } = "/api/direct-agent/events";
}
