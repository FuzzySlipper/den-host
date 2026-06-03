namespace DenHost.Clients;

/// <summary>
/// One row in a Channels event list response. Mirrors the wire
/// shape of den-channels <c>GatewayEventItemDto</c> (which is the
/// current list response for both /api/gateway/events and the
/// single-event GET /api/direct-agent-events/{eventId} readback).
/// The shape is intentionally flat -- the source/target split is
/// implicit in field naming (SourceXxx / TargetXxx).
/// All fields are optional in the wire protocol; the matcher only
/// looks at a small subset.
/// </summary>
public sealed record ChannelsEvent(
    long EventId,
    long ChannelId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string? SourceKind,
    string? SourceId,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    string? DeliveryRequestId,
    string? DedupeKey,
    string? DeepLink,
    string? Summary,
    string? Body,
    DateTimeOffset CreatedAt);

/// <summary>
/// One page of Channels events. The cursor is the long message id
/// from the last row of the previous page (or null on the first read).
/// </summary>
/// <param name="Items">Events in the page, in the order returned by Channels (typically ascending by id).</param>
/// <param name="NextAfterId">
/// Long message id to pass on the next call to resume after this page.
/// Null when the page exhausted the stream.
/// </param>
/// <param name="HasMore">Channels hint that more events are available.</param>
/// <param name="EndpointImplemented">
/// False if the endpoint returned 404 (or its equivalent) so the
/// reader should report "endpoint not implemented" rather than
/// silently treating an empty page as caught-up. Set to true when
/// the page came from a live implementation.
/// </param>
public sealed record ChannelsEventPage(
    IReadOnlyList<ChannelsEvent> Items,
    long? NextAfterId,
    bool HasMore,
    bool EndpointImplemented);

/// <summary>
/// Single-event readback from GET /api/direct-agent-events/{eventId}.
/// Includes the full attribution set (source / target / session /
/// delivery / claim / completion) and a free-text body. Used by the
/// future wake path's "I have an event id, fetch its details" flow.
/// </summary>
public sealed record ChannelsEventReadback(
    long EventId,
    long ChannelId,
    string RequestId,
    string MessageKind,
    string SenderType,
    string SenderIdentity,
    string MemberIdentity,
    string WakePolicy,
    string? SourceKind,
    string? SourceProjectId,
    string? TargetProjectId,
    long? TargetTaskId,
    string? AssignmentId,
    string? WorkerRunId,
    string? WorkerRole,
    string? ProfileIdentity,
    string? PoolMemberId,
    string? AgentInstanceId,
    string? SessionOwnerId,
    string? SessionId,
    string? Summary,
    string Body,
    string? DeliveryStatus,
    string? ClaimStatus,
    string? CompletionStatus,
    DateTimeOffset CreatedAt);

/// <summary>
/// HTTP client contract for the Channels endpoint.
/// </summary>
public interface IChannelsClient
{
    /// <summary>
    /// Probes the Channels health endpoint and returns a structured
    /// reachability result. Never throws on connectivity failure.
    /// </summary>
    Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads a page of direct-agent events from the configured list
    /// endpoint. Used by the shadow-mode reader (den-host task #1916).
    /// The query is scoped by either <paramref name="channelId"/> or
    /// <paramref name="projectId"/>; <paramref name="afterId"/> is
    /// the long message id from the last row of the previous page
    /// (or null on the first read). The reader writes the next
    /// afterId to its cursor store on a successful read.
    /// </summary>
    Task<ChannelsEventPage> GetDirectAgentEventsAsync(
        long? channelId,
        string? projectId,
        long? afterId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a single direct-agent event by id from the primary
    /// Channels-owned readback (GET /api/direct-agent-events/{eventId}).
    /// Returns null if the event is not found.
    /// </summary>
    Task<ChannelsEventReadback?> GetDirectAgentEventAsync(
        long eventId,
        CancellationToken cancellationToken);
}
