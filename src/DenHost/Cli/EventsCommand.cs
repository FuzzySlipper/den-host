using System.Text.Json;
using DenHost.Channels;
using DenHost.Cli.Hosting;
using DenHost.Clients;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host events tail</c> command. Reads a single page
/// of Channels direct-agent events in shadow mode and prints each
/// event with its match outcome. Useful for debugging the channel
/// stream and the host's matching logic.
/// </summary>
public sealed class EventsCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannelsEventReader _reader;
    private readonly ICliHost _cliHost;

    public EventsCommand(IChannelsEventReader reader, ICliHost cliHost)
    {
        _reader = reader;
        _cliHost = cliHost;
    }

    public string Name => "events";

    public string Summary => "Read Channels direct-agent events in shadow mode (subcommand: 'tail').";

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
            "-h" or "--help" => PrintHelpAndOk(context),
            _ => FailUnknownSubcommand(context),
        };
    }

    private int PrintHelpAndOk(CliContext context)
    {
        PrintHelp(context.Host);
        return 0;
    }

    private async Task<int> ExecuteTailAsync(CliContext context, CancellationToken cancellationToken)
    {
        var asJson = false;
        var pageSize = 50;
        for (var i = 1; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host events tail [--json] [--limit N]");
                    return 0;
                case "--limit":
                    if (i + 1 >= context.Args.Count)
                    {
                        context.Host.WriteErrorLine("events: --limit requires a value");
                        return 2;
                    }
                    if (!int.TryParse(context.Args[++i], out pageSize) || pageSize <= 0)
                    {
                        context.Host.WriteErrorLine($"events: --limit must be a positive integer; got '{context.Args[i]}'");
                        return 2;
                    }
                    break;
                default:
                    if (context.Args[i].StartsWith("--limit=", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(context.Args[i]["--limit=".Length..], out pageSize) || pageSize <= 0)
                        {
                            context.Host.WriteErrorLine($"events: --limit must be a positive integer; got '{context.Args[i]}'");
                            return 2;
                        }
                    }
                    else
                    {
                        context.Host.WriteErrorLine($"events: unknown argument '{context.Args[i]}'");
                        return 2;
                    }
                    break;
            }
        }

        var result = await _reader.ReadPageAsync(pageSize, cancellationToken).ConfigureAwait(false);

        if (asJson)
        {
            context.Host.WriteLine(FormatJson(result));
        }
        else
        {
            context.Host.WriteLine(FormatText(result));
        }

        // 0 = endpoint implemented, 1 = endpoint missing (or the page was empty and the endpoint is missing).
        return result.EndpointImplemented ? 0 : 5;
    }

    private void PrintHelp(ICliHost host)
    {
        host.WriteLine("Usage: den-host events <subcommand> [args]");
        host.WriteLine("");
        host.WriteLine("Subcommands:");
        host.WriteLine("  tail [--json] [--limit N]   Read a single page of events in shadow mode.");
    }

    private int FailUnknownSubcommand(CliContext context)
    {
        context.Host.WriteErrorLine($"events: unknown subcommand '{context.Args[0]}'");
        PrintHelp(context.Host);
        return 2;
    }

    private static string FormatText(ChannelsEventReadResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Channels events (read {result.Page.Events.Count} of page; cursor_next={result.Page.NextCursor ?? "-"}; endpoint_implemented={result.EndpointImplemented})");
        if (result.Outcomes.Count == 0)
        {
            sb.AppendLine("  (no events on this page)");
        }
        for (var i = 0; i < result.Page.Events.Count; i++)
        {
            var evt = result.Page.Events[i];
            var outcome = result.Outcomes[i];
            var mark = outcome.IsForUs ? "FOR-US" : "skip  ";
            var action = outcome.IntendedAction ?? "-";
            sb.AppendLine(
                $"  [{mark}] {outcome.EventId} reason={outcome.Reason} intended={action} " +
                $"project={evt.Source.ProjectId ?? "-"} task={evt.Source.TaskId?.ToString() ?? "-"} " +
                $"target.pool_member={evt.Target.PoolMemberId ?? "-"} target.role={evt.Target.Role ?? "-"} " +
                $"target.assignment={evt.Target.AssignmentId?.ToString() ?? "-"} target.run={evt.Target.RunId ?? "-"}");
        }
        return sb.ToString();
    }

    private static string FormatJson(ChannelsEventReadResult result) => JsonSerializer.Serialize(new
    {
        endpointImplemented = result.EndpointImplemented,
        cursorNext = result.Page.NextCursor,
        events = result.Page.Events.Select((evt, i) => new
        {
            eventId = evt.EventId,
            createdAt = evt.CreatedAt,
            sender = evt.Sender,
            source = evt.Source,
            target = evt.Target,
            replyContext = evt.ReplyContext,
            match = new
            {
                isForUs = result.Outcomes[i].IsForUs,
                reason = result.Outcomes[i].Reason,
                intendedAction = result.Outcomes[i].IntendedAction,
                wouldWake = result.Outcomes[i].WouldWake,
                migrationDiffNote = result.Outcomes[i].MigrationDiffNote,
            },
        }),
    }, s_jsonOptions);
}
