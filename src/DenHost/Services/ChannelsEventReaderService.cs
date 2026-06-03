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
/// every wake event (with the migration-diff note), and never launches
/// a worker. The service is the long-lived form of
/// <c>den-host events tail</c>; the one-shot CLI uses the same reader.
/// </summary>
public sealed class ChannelsEventReaderService : BackgroundService
{
    private readonly IChannelsEventReader _reader;
    private readonly ChannelsOptions _channels;
    private readonly RuntimeOptions _runtime;
    private readonly ILogger<ChannelsEventReaderService> _logger;

    public ChannelsEventReaderService(
        IChannelsEventReader reader,
        ChannelsOptions channels,
        RuntimeOptions runtime,
        ILogger<ChannelsEventReaderService> logger)
    {
        _reader = reader;
        _channels = channels;
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
            "Channels event reader starting; interval={Seconds}s page_size={PageSize} channel_id={ChannelId} project_id={ProjectId}",
            intervalSeconds, pageSize, _channels.EventsListChannelId, _channels.EventsListProjectId);

        var consecutiveEndpointMissing = 0;
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var query = new ChannelsEventReadQuery(
                        ChannelId: _channels.EventsListChannelId,
                        ProjectId: _channels.EventsListProjectId,
                        AfterId: null,
                        PageSize: pageSize);
                    var result = await _reader.ReadPageAsync(query, stoppingToken).ConfigureAwait(false);
                    if (!result.EndpointImplemented)
                    {
                        consecutiveEndpointMissing++;
                        if (consecutiveEndpointMissing == 1 || consecutiveEndpointMissing % 5 == 0)
                        {
                            _logger.LogWarning(
                                "Channels list endpoint not implemented (consecutive_missing={Count}). " +
                                "The shadow reader will keep polling. " +
                                "Use 'den-host events tail' to inspect manually.",
                                consecutiveEndpointMissing);
                        }
                        continue;
                    }
                    consecutiveEndpointMissing = 0;

                    if (result.Page.Items.Count == 0)
                    {
                        _logger.LogDebug(
                            "Channels event read: 0 items; after_id={AfterId}",
                            result.Page.NextAfterId);
                        continue;
                    }

                    foreach (var outcome in result.Outcomes)
                    {
                        var evt = result.Page.Items.First(e => e.EventId == outcome.EventId);
                        LogOutcome(evt, outcome);
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

    private void LogOutcome(ChannelsEvent evt, EventMatchOutcome outcome)
    {
        var targetDesc = DescribeTarget(evt);
        var sourceDesc = DescribeSource(evt);
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

    private static string DescribeTarget(ChannelsEvent evt) =>
        $"pool_member={evt.PoolMemberId ?? "-"} worker_role={evt.WorkerRole ?? "-"} assignment={evt.AssignmentId ?? "-"} worker_run={evt.WorkerRunId ?? "-"}";

    private static string DescribeSource(ChannelsEvent evt) =>
        $"project={evt.SourceProjectId ?? "-"} target_project={evt.TargetProjectId ?? "-"} target_task={evt.TargetTaskId?.ToString() ?? "-"} sender={evt.SenderIdentity}";
}
