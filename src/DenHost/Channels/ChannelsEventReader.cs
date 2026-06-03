using DenHost.Clients;
using DenHost.Host;
using Microsoft.Extensions.Logging;

namespace DenHost.Channels;

/// <summary>
/// Reads a single page of Channels direct-agent events and produces a
/// list of match outcomes. Pure (apart from the Channels HTTP call and
/// the local cursor file): no worker launch, no harness module invocation.
/// Used by both the <c>den-host events tail</c> one-shot CLI command
/// and the <c>ChannelsEventReaderService</c> background service.
/// </summary>
public interface IChannelsEventReader
{
    Task<ChannelsEventReadResult> ReadPageAsync(int pageSize, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a single Channels event read.
/// </summary>
/// <param name="Page">
/// The page returned by Channels. Events is empty if the endpoint is missing
/// or returned 404; NextCursor is the cursor to pass on the next call.
/// </param>
/// <param name="Outcomes">
/// One match outcome per event, in the same order as the page.
/// </param>
/// <param name="EndpointImplemented">
/// False if the Channels endpoint returned 404 (or its equivalent), meaning
/// the den-channels #1902 contract is not yet live. The reader should log
/// this so an operator can see the situation without trawling logs.
/// </param>
public sealed record ChannelsEventReadResult(
    DirectAgentEventPage Page,
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

    public async Task<ChannelsEventReadResult> ReadPageAsync(int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }

        var cursor = await _cursorStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        DirectAgentEventPage page;
        try
        {
            page = await _channels.GetDirectAgentEventsAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Channels direct-agent event read failed; cursor={Cursor}", cursor);
            // Bubble out as an "endpoint missing" result so the caller can
            // decide whether to retry or surface as a blocker. We do NOT
            // advance the cursor on a failed read.
            return new ChannelsEventReadResult(
                new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), cursor),
                Array.Empty<EventMatchOutcome>(),
                EndpointImplemented: false);
        }

        var outcomes = new List<EventMatchOutcome>(page.Events.Count);
        foreach (var evt in page.Events)
        {
            outcomes.Add(EventMatcher.Match(evt, _identity));
        }

        // Advance the cursor only on a successful read, even if the page is
        // empty (the endpoint exists; the host is just caught up). This
        // matches the typical cursor-advances-on-empty-page contract.
        if (page.NextCursor is not null)
        {
            await _cursorStore.WriteAsync(page.NextCursor, cancellationToken).ConfigureAwait(false);
        }

        return new ChannelsEventReadResult(page, outcomes, EndpointImplemented: true);
    }
}
