using DenHost.Channels;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DenHost.Services;

/// <summary>
/// Background service that polls Channels for direct-agent events in
/// shadow mode. Each tick reads a page, logs the match outcomes for
/// every event (with the migration-diff note), and never launches a
/// worker. The service is the long-lived form of
/// <c>den-host events tail</c>; the one-shot CLI uses the same reader.
/// </summary>
public sealed class ChannelsEventReaderService : BackgroundService
{
    private readonly IChannelsEventReader _reader;
    private readonly RuntimeOptions _runtime;
    private readonly ILogger<ChannelsEventReaderService> _logger;

    public ChannelsEventReaderService(
        IChannelsEventReader reader,
        RuntimeOptions runtime,
        ILogger<ChannelsEventReaderService> logger)
    {
        _reader = reader;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _runtime.ChannelsEventPollSeconds;
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation(
                "Channels event reader disabled (Runtime:ChannelsEventPollSeconds={Seconds}).",
                intervalSeconds);
            return;
        }

        var pageSize = _runtime.ChannelsEventPageSize;
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        _logger.LogInformation(
            "Channels event reader starting; interval={Seconds}s page_size={PageSize}",
            intervalSeconds, pageSize);

        var consecutiveEndpointMissing = 0;
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var result = await _reader.ReadPageAsync(pageSize, stoppingToken).ConfigureAwait(false);
                    if (!result.EndpointImplemented)
                    {
                        consecutiveEndpointMissing++;
                        if (consecutiveEndpointMissing == 1 || consecutiveEndpointMissing % 5 == 0)
                        {
                            _logger.LogWarning(
                                "Channels direct-agent event endpoint not implemented (consecutive_missing={Count}). " +
                                "den-channels #1902 may not be live yet; the shadow reader will keep polling. " +
                                "Use the one-shot 'den-host events tail' to inspect manually.",
                                consecutiveEndpointMissing);
                        }
                        continue;
                    }
                    consecutiveEndpointMissing = 0;

                    if (result.Page.Events.Count == 0)
                    {
                        _logger.LogDebug("Channels event read: 0 events; cursor={Cursor}", result.Page.NextCursor);
                        continue;
                    }

                    foreach (var outcome in result.Outcomes)
                    {
                        LogOutcome(result.Page.Events.First(e => e.EventId == outcome.EventId), outcome);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Channels event read threw unexpectedly; will retry on next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void LogOutcome(DirectAgentEvent evt, EventMatchOutcome outcome)
    {
        var targetDesc = DescribeTarget(evt.Target);
        var sourceDesc = DescribeSource(evt.Source);
        if (outcome.IsForUs)
        {
            _logger.LogInformation(
                "shadow: event {EventId} FOR US (reason={Reason}, intended={Intended}, target={Target}, source={Source}). {Note}",
                outcome.EventId, outcome.Reason, outcome.IntendedAction ?? "-", targetDesc, sourceDesc, outcome.MigrationDiffNote);
        }
        else
        {
            _logger.LogInformation(
                "shadow: event {EventId} not for us (reason={Reason}, target={Target}, source={Source}). {Note}",
                outcome.EventId, outcome.Reason, targetDesc, sourceDesc, outcome.MigrationDiffNote);
        }
    }

    private static string DescribeTarget(TargetWork target) =>
        $"pool_member={target.PoolMemberId ?? "-"} role={target.Role ?? "-"} assignment={target.AssignmentId?.ToString() ?? "-"} run={target.RunId ?? "-"}";

    private static string DescribeSource(SourceContext source) =>
        $"project={source.ProjectId ?? "-"} task={source.TaskId?.ToString() ?? "-"} message={source.MessageId?.ToString() ?? "-"} room={source.RoomId?.ToString() ?? "-"}";
}
