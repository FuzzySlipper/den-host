using System.Text.Json;
using DenHost.Channels;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Configuration;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host events tail|get</c> command.
/// <c>tail</c> reads a page from the configured list endpoint
/// (transitional /api/gateway/events); <c>get &lt;eventId&gt;</c>
/// reads a single event from the primary Channels readback
/// (GET /api/direct-agent-events/{eventId}). Both are shadow-mode:
/// the reader never launches a worker.
/// </summary>
public sealed class EventsCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannelsEventReader _reader;
    private readonly IChannelsClient _channels;
    private readonly ChannelsOptions _channelsOptions;
    private readonly ICliHost _cliHost;

    public EventsCommand(
        IChannelsEventReader reader,
        IChannelsClient channels,
        ChannelsOptions channelsOptions,
        ICliHost cliHost)
    {
        _reader = reader;
        _channels = channels;
        _channelsOptions = channelsOptions;
        _cliHost = cliHost;
    }

    public string Name => "events";

    public string Summary => "Read Channels events in shadow mode (subcommands: 'tail', 'get').";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        if (context.Args.Count == 0)
        {
            PrintHelp(context.Host);
            return 0;
        }

        return context.Args[0] switch
        {
            "tail" => await ExecuteTailAsync(context, cancellationToken).ConfigureAwait(false),
            "get" => await ExecuteGetAsync(context, cancellationToken).ConfigureAwait(false),
            "-h" or "--help" => PrintHelpAndOk(context),
            _ => UnknownAsync(context),
        };
    }

    private int PrintHelpAndOk(CliContext context)
    {
        PrintHelp(context.Host);
        return 0;
    }

    private int UnknownAsync(CliContext context)
    {
        context.Host.WriteErrorLine($"events: unknown subcommand '{context.Args[0]}'");
        PrintHelp(context.Host);
        return 2;
    }

    private async Task<int> ExecuteTailAsync(CliContext context, CancellationToken cancellationToken)
    {
        long? channelId = _channelsOptions.EventsListChannelId;
        string? projectId = _channelsOptions.EventsListProjectId;
        long? afterId = null;
        int pageSize = 50;
        var asJson = false;

        for (var i = 1; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "--channel-id":
                    if (i + 1 >= context.Args.Count) { context.Host.WriteErrorLine("events: --channel-id requires a value"); return 2; }
                    if (!long.TryParse(context.Args[++i], out var cid)) { context.Host.WriteErrorLine($"events: --channel-id must be a long; got '{context.Args[i]}'"); return 2; }
                    channelId = cid;
                    break;
                case "--project-id":
                    if (i + 1 >= context.Args.Count) { context.Host.WriteErrorLine("events: --project-id requires a value"); return 2; }
                    projectId = context.Args[++i];
                    break;
                case "--after-id":
                    if (i + 1 >= context.Args.Count) { context.Host.WriteErrorLine("events: --after-id requires a value"); return 2; }
                    if (!long.TryParse(context.Args[++i], out var aid)) { context.Host.WriteErrorLine($"events: --after-id must be a long; got '{context.Args[i]}'"); return 2; }
                    afterId = aid;
                    break;
                case "--limit":
                    if (i + 1 >= context.Args.Count) { context.Host.WriteErrorLine("events: --limit requires a value"); return 2; }
                    if (!int.TryParse(context.Args[++i], out pageSize) || pageSize <= 0) { context.Host.WriteErrorLine($"events: --limit must be a positive integer; got '{context.Args[i]}'"); return 2; }
                    break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host events tail [--channel-id N] [--project-id P] [--after-id N] [--limit N] [--json]");
                    return 0;
                default:
                    context.Host.WriteErrorLine($"events: unknown argument '{context.Args[i]}'");
                    return 2;
            }
        }

        if (channelId is null && string.IsNullOrWhiteSpace(projectId))
        {
            context.Host.WriteErrorLine("events: provide --channel-id or --project-id (or set Channels:EventsListChannelId / EventsListProjectId in den-host.json)");
            return 2;
        }

        var query = new ChannelsEventReadQuery(channelId, projectId, afterId, pageSize);
        var result = await _reader.ReadPageAsync(query, cancellationToken).ConfigureAwait(false);

        if (asJson)
        {
            context.Host.WriteLine(FormatTailJson(result));
        }
        else
        {
            context.Host.WriteLine(FormatTailText(result));
        }
        // 0 = endpoint implemented, 5 = endpoint missing.
        return result.EndpointImplemented ? 0 : 5;
    }

    private async Task<int> ExecuteGetAsync(CliContext context, CancellationToken cancellationToken)
    {
        if (context.Args.Count < 2)
        {
            context.Host.WriteErrorLine("events get: missing <eventId> argument");
            PrintHelp(context.Host);
            return 2;
        }
        if (!long.TryParse(context.Args[1], out var eventId) || eventId <= 0)
        {
            context.Host.WriteErrorLine($"events get: <eventId> must be a positive long; got '{context.Args[1]}'");
            return 2;
        }
        var asJson = false;
        for (var i = 2; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json": asJson = true; break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host events get <eventId> [--json]");
                    return 0;
                default:
                    context.Host.WriteErrorLine($"events get: unknown argument '{context.Args[i]}'");
                    return 2;
            }
        }

        var readback = await _channels.GetDirectAgentEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (readback is null)
        {
            context.Host.WriteErrorLine($"events get: event {eventId} not found");
            return 4;
        }
        if (asJson)
        {
            context.Host.WriteLine(JsonSerializer.Serialize(readback, s_jsonOptions));
        }
        else
        {
            context.Host.WriteLine(FormatGetText(readback));
        }
        return 0;
    }

    private void PrintHelp(ICliHost host)
    {
        host.WriteLine("Usage: den-host events <subcommand> [args]");
        host.WriteLine("");
        host.WriteLine("Subcommands:");
        host.WriteLine("  tail [--channel-id N] [--project-id P] [--after-id N] [--limit N] [--json]");
        host.WriteLine("      Read a page of wake_event items from the configured list endpoint (transitional /api/gateway/events).");
        host.WriteLine("  get <eventId> [--json]");
        host.WriteLine("      Read a single direct-agent event from the primary Channels readback (GET /api/direct-agent-events/{eventId}).");
    }

    private static string FormatTailText(ChannelsEventReadResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"Channels events (read {result.Page.Items.Count} of page; after_id={result.Page.NextAfterId?.ToString() ?? "-"}; has_more={result.Page.HasMore}; endpoint_implemented={result.EndpointImplemented})");
        if (result.Outcomes.Count == 0 && result.Page.Items.Count > 0)
        {
            sb.AppendLine("  (no wake_event items on this page; non-wake messages filtered out)");
        }
        else if (result.Outcomes.Count == 0)
        {
            sb.AppendLine("  (no events on this page)");
        }
        for (var i = 0; i < result.Page.Items.Count; i++)
        {
            var evt = result.Page.Items[i];
            var outcome = result.Outcomes.FirstOrDefault(o => o.EventId == evt.EventId);
            if (outcome is null)
            {
                sb.AppendLine($"  [skip  ] {evt.EventId} source_kind={evt.SourceKind ?? "-"} message_kind={evt.MessageKind} (not a wake_event)");
                continue;
            }
            var mark = outcome.IsForUs ? "FOR-US" : "skip  ";
            var action = outcome.IntendedAction ?? "-";
            sb.AppendLine(
                $"  [{mark}] {outcome.EventId} reason={outcome.Reason} intended={action} " +
                $"pool_member={evt.PoolMemberId ?? "-"} worker_role={evt.WorkerRole ?? "-"} " +
                $"assignment={evt.AssignmentId ?? "-"} worker_run={evt.WorkerRunId ?? "-"} " +
                $"source_project={evt.SourceProjectId ?? "-"} target_project={evt.TargetProjectId ?? "-"} " +
                $"target_task={evt.TargetTaskId?.ToString() ?? "-"} sender={evt.SenderIdentity}");
        }
        return sb.ToString();
    }

    private static string FormatTailJson(ChannelsEventReadResult result) => JsonSerializer.Serialize(new
    {
        endpointImplemented = result.EndpointImplemented,
        afterId = result.Page.NextAfterId,
        hasMore = result.Page.HasMore,
        events = result.Page.Items.Select(evt => new
        {
            eventId = evt.EventId,
            channelId = evt.ChannelId,
            messageKind = evt.MessageKind,
            senderType = evt.SenderType,
            senderIdentity = evt.SenderIdentity,
            source = new
            {
                kind = evt.SourceKind,
                id = evt.SourceId,
                projectId = evt.SourceProjectId,
            },
            target = new
            {
                projectId = evt.TargetProjectId,
                taskId = evt.TargetTaskId,
                assignmentId = evt.AssignmentId,
                workerRunId = evt.WorkerRunId,
                workerRole = evt.WorkerRole,
                profileIdentity = evt.ProfileIdentity,
                poolMemberId = evt.PoolMemberId,
                agentInstanceId = evt.AgentInstanceId,
                sessionOwnerId = evt.SessionOwnerId,
                sessionId = evt.SessionId,
            },
            delivery = new
            {
                requestId = evt.DeliveryRequestId,
                dedupeKey = evt.DedupeKey,
                deepLink = evt.DeepLink,
            },
            summary = evt.Summary,
            body = evt.Body,
            createdAt = evt.CreatedAt,
            match = result.Outcomes.FirstOrDefault(o => o.EventId == evt.EventId) is { } o
                ? new
                {
                    isForUs = o.IsForUs,
                    reason = o.Reason,
                    intendedAction = o.IntendedAction,
                    wouldWake = o.WouldWake,
                    migrationDiffNote = o.MigrationDiffNote,
                }
                : null,
        }),
    }, s_jsonOptions);

    private static string FormatGetText(ChannelsEventReadback r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Channels event {r.EventId} (channel {r.ChannelId}, member={r.MemberIdentity})");
        sb.AppendLine($"  request_id     = {r.RequestId}");
        sb.AppendLine($"  message_kind   = {r.MessageKind}");
        sb.AppendLine($"  sender         = {r.SenderType}/{r.SenderIdentity}");
        sb.AppendLine($"  wake_policy    = {r.WakePolicy}");
        sb.AppendLine($"  source         = kind={r.SourceKind ?? "-"} project={r.SourceProjectId ?? "-"}");
        sb.AppendLine($"  target         = project={r.TargetProjectId ?? "-"} task={r.TargetTaskId?.ToString() ?? "-"} " +
            $"assignment={r.AssignmentId ?? "-"} worker_run={r.WorkerRunId ?? "-"} worker_role={r.WorkerRole ?? "-"}");
        sb.AppendLine($"  identity       = pool_member={r.PoolMemberId ?? "-"} profile={r.ProfileIdentity ?? "-"} " +
            $"agent_instance={r.AgentInstanceId ?? "-"} session_owner={r.SessionOwnerId ?? "-"} session={r.SessionId ?? "-"}");
        sb.AppendLine($"  delivery       = status={r.DeliveryStatus ?? "-"} claim={r.ClaimStatus ?? "-"} completion={r.CompletionStatus ?? "-"}");
        sb.AppendLine($"  summary        = {r.Summary ?? "-"}");
        sb.AppendLine($"  body           = {r.Body}");
        sb.AppendLine($"  created_at     = {r.CreatedAt:O}");
        return sb.ToString();
    }
}
