namespace DenHost.Clients;

/// <summary>
/// Source-context metadata attached to a direct-agent Channels event.
/// All fields are Den-facing: no Hermes profile, no harness-specific
/// session/process/path data leaks through this shape.
/// </summary>
/// <param name="ProjectId">Project the event belongs to (e.g. "den-host").</param>
/// <param name="TaskId">Optional task id the event targets.</param>
/// <param name="MessageId">Channels message id, when sourced from a message.</param>
/// <param name="RoomId">Channels room id, when sourced from a room activity.</param>
/// <param name="ActivityId">Channels activity id, when sourced from an activity record.</param>
public sealed record SourceContext(
    string? ProjectId,
    int? TaskId,
    int? MessageId,
    int? RoomId,
    int? ActivityId);

/// <summary>
/// Target-work metadata attached to a direct-agent Channels event.
/// Identifies the assignment / run / pool member the event wants woken.
/// </summary>
/// <param name="AssignmentId">Core assignment id, if assigned.</param>
/// <param name="RunId">Core worker-run id, if a run is registered.</param>
/// <param name="PoolMemberId">Pool member id, if a pool member is the target.</param>
/// <param name="Role">Generic Den role name (e.g. "coder", "reviewer").</param>
public sealed record TargetWork(
    int? AssignmentId,
    string? RunId,
    string? PoolMemberId,
    string? Role);

/// <summary>
/// A single direct-agent Channels event the host can shadow-read.
/// </summary>
/// <param name="EventId">Server-side event id (used as cursor).</param>
/// <param name="CreatedAt">Server-side creation timestamp.</param>
/// <param name="Sender">Logical sender identity (e.g. user identity, agent identity).</param>
/// <param name="ReplyContext">Optional reply context (parent message id, etc.).</param>
/// <param name="Source">Source-context metadata.</param>
/// <param name="Target">Target-work metadata.</param>
public sealed record DirectAgentEvent(
    string EventId,
    DateTimeOffset CreatedAt,
    string Sender,
    ReplyContext? ReplyContext,
    SourceContext Source,
    TargetWork Target);

/// <summary>
/// Optional reply context attached to a direct-agent event.
/// </summary>
public sealed record ReplyContext(int? ParentMessageId, string? ThreadId);

/// <summary>
/// Page of direct-agent events read from Channels, plus the cursor
/// that should be passed on the next call to resume after this page.
/// </summary>
/// <param name="Events">Events in the page, ordered by event id ascending.</param>
/// <param name="NextCursor">Cursor for the next page; null if the page exhausted the stream.</param>
public sealed record DirectAgentEventPage(
    IReadOnlyList<DirectAgentEvent> Events,
    string? NextCursor);

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
    /// Reads a page of direct-agent events. Used by the shadow-mode
    /// event reader (den-host task #1916). The cursor is opaque
    /// and is round-tripped as a string.
    /// </summary>
    /// <param name="cursor">
    /// Opaque cursor returned by a previous call. Null to read from
    /// the start of the stream (or as far back as Channels retains).
    /// </param>
    /// <param name="limit">
    /// Maximum events to return in this page. Caller-chosen, host-side.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DirectAgentEventPage> GetDirectAgentEventsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken);
}
