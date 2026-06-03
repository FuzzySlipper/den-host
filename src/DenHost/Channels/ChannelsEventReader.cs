using DenHost.Clients;
using DenHost.Host;
using Microsoft.Extensions.Logging;

namespace DenHost.Channels;

/// <summary>
/// Reads a single page of Channels direct-agent events and produces
/// a list of match outcomes. Pure (apart from the Channels HTTP
/// call and the local cursor file): no worker launch, no harness
/// module invocation. Used by both the <c>den-host events tail</c>
/// one-shot CLI command and the <c>ChannelsEventReaderService</c>
/// background service.
/// </summary>
public interface IChannelsEventReader
{
    Task<ChannelsEventReadResult> ReadPageAsync(ChannelsEventReadQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Query for a single Channels event read. At least one of
/// <see cref="ChannelId"/> or <see cref="ProjectId"/> must be set;
/// the underlying endpoint requires it.
/// </summary>
public sealed record ChannelsEventReadQuery(
    long? ChannelId,
    string? ProjectId,
    long? AfterId,
    int PageSize);

/// <summary>
/// Result of a single Channels event read.
/// </summary>
/// <param name="Page">The page returned by Channels.</param>
/// <param name="Outcomes">One match outcome per wake_event item, in the same order as the page.</param>
/// <param name="EndpointImplemented">
/// False if the Channels endpoint returned 404 (or its equivalent), meaning
/// the den-channels #1902 contract is not yet live. The reader should log
/// this so an operator can see the situation without trawling logs.
/// </param>
public sealed record ChannelsEventReadResult(
    ChannelsEventPage Page,
    IReadOnlyList<EventMatchOutcome> Outcomes,
    bool EndpointImplemented);

internal sealed class ChannelsEventReader : IChannelsEventReader
{
    private readonly IChannelsClient _channels;
    private readonly EventCursorStore _cursorStore;
    private readonly AdapterIdentity _identity;
    private readonly ILogger<ChannelsEventReader> _logger;

    public ChannelsEventReader(
        IChannelsClient channels,
        EventCursorStore cursorStore,
        AdapterIdentity identity,
        ILogger<ChannelsEventReader> logger)
    {
        _channels = channels;
        _cursorStore = cursorStore;
        _identity = identity;
        _logger = logger;
    }

    public async Task<ChannelsEventReadResult> ReadPageAsync(ChannelsEventReadQuery query, CancellationToken cancellationToken)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "PageSize must be positive.");
        }

        // If the caller did not supply an explicit afterId, fall back to the
        // cursor on disk. This is the normal "background polling" path.
        var afterId = query.AfterId ?? await _cursorStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        ChannelsEventPage page;
        try
        {
            page = await _channels.GetDirectAgentEventsAsync(
                query.ChannelId, query.ProjectId, afterId, query.PageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Channels list read failed; after_id={AfterId}", afterId);
            return new ChannelsEventReadResult(
                new ChannelsEventPage(Array.Empty<ChannelsEvent>(), afterId, HasMore: false, EndpointImplemented: false),
                Array.Empty<EventMatchOutcome>(),
                EndpointImplemented: false);
        }

        // Only wake_events get matched; everything else is filtered before
        // the matcher runs so the matcher's "for us / not for us" decision
        // is meaningful for the wake path.
        var outcomes = new List<EventMatchOutcome>(page.Items.Count);
        foreach (var evt in page.Items)
        {
            if (!string.Equals(evt.SourceKind, "wake_event", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            outcomes.Add(EventMatcher.Match(evt, _identity));
        }

        // Advance the cursor on a successful read, even if the page is
        // empty (the endpoint exists; the host is just caught up).
        if (page.NextAfterId is long nextId)
        {
            await _cursorStore.WriteAsync(nextId, cancellationToken).ConfigureAwait(false);
        }

        return new ChannelsEventReadResult(page, outcomes, EndpointImplemented: page.EndpointImplemented);
    }
}
