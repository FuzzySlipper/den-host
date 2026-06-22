using System.ComponentModel.DataAnnotations;

namespace DenHost.Configuration;

/// <summary>
/// Base shape for a Den endpoint. Concrete subclasses bind to named
/// sections of the den-host.json config and provide type-safe accessors.
/// </summary>
public abstract class EndpointOptions
{
    /// <summary>
    /// Absolute base URL of the endpoint, e.g. "http://127.0.0.1:5299".
    /// Must be an absolute URI. Loopback is preferred for machine-local
    /// probes so den-host does not require ordinary agents to know LAN topology.
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
/// Optional legacy Channels compatibility/archive endpoint.
///
/// Den-host no longer treats den-channels direct-agent routes as an active
/// worker wake or coordination contract. New executable wakes belong to the
/// Delivery successor and human-facing transcript/readback belongs to the
/// Conversation/Timeline successors. Keep these options empty unless an
/// operator deliberately needs cold-history readback for old den-channels
/// evidence.
/// </summary>
public sealed class ChannelsOptions : EndpointOptions
{
    public const string SectionName = "Channels";

    /// <summary>
    /// Optional legacy list endpoint used only for explicit cold-history
    /// readback. Empty by default so den-host can run with den-channels
    /// legacy direct-agent routes unavailable.
    /// </summary>
    public string EventsListPath { get; init; } = "";

    /// <summary>
    /// Optional channel id to scope the list read. If null, the
    /// reader falls back to <see cref="EventsListProjectId"/>.
    /// </summary>
    public long? EventsListChannelId { get; init; }

    /// <summary>
    /// Optional project id to resolve a default channel for the list
    /// read. Only used when <see cref="EventsListChannelId"/> is null.
    /// </summary>
    public string? EventsListProjectId { get; init; }

    /// <summary>
    /// Optional legacy single-event readback path. Empty by default;
    /// den-host is not the active wake reader.
    /// </summary>
    public string DirectAgentEventPath { get; init; } = "";

    /// <summary>
    /// Optional legacy machine-written lifecycle event producer path.
    /// Empty by default because den-host no longer publishes active worker
    /// lifecycle through den-channels.
    /// </summary>
    public string AgentWorkLifecyclePath { get; init; } = "";
}
