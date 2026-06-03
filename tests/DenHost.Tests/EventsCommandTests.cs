using DenHost.Channels;
using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Clients;

namespace DenHost.Tests;

public class EventsCommandTests
{
    [Fact]
    public async Task ExecuteAsync_NoSubcommand_PrintsHelp()
    {
        var cmd = new EventsCommand(new FixedReader(MakeEmptyResult(true)), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Usage: den-host events", ((BufferingCliHost)context.Host).StandardOut);
    }

    [Fact]
    public async Task Tail_WithImplementedEndpoint_Exits0()
    {
        var result = new ChannelsEventReadResult(
            new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null),
            Array.Empty<EventMatchOutcome>(),
            EndpointImplemented: true);
        var cmd = new EventsCommand(new FixedReader(result), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Tail_WithMissingEndpoint_Exits5()
    {
        var result = new ChannelsEventReadResult(
            new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null),
            Array.Empty<EventMatchOutcome>(),
            EndpointImplemented: false);
        var cmd = new EventsCommand(new FixedReader(result), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(5, exit);
    }

    [Fact]
    public async Task Tail_JsonOutput_ContainsKey()
    {
        var evt = new DirectAgentEvent(
            EventId: "evt-7",
            CreatedAt: DateTimeOffset.UtcNow,
            Sender: "user-1",
            ReplyContext: null,
            Source: new SourceContext("den-host", null, null, null, null),
            Target: new TargetWork(null, null, "den-host-01", null));
        var outcome = new EventMatchOutcome(
            EventId: "evt-7", IsForUs: true, Reason: "pool_member_match",
            IntendedAction: "wake", MigrationDiffNote: "delta");
        var result = new ChannelsEventReadResult(
            new DirectAgentEventPage(new[] { evt }, "next"),
            new[] { outcome },
            EndpointImplemented: true);
        var cmd = new EventsCommand(new FixedReader(result), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "tail", "--json" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("\"eventId\": \"evt-7\"", output);
        Assert.Contains("\"isForUs\": true", output);
        Assert.Contains("\"wouldWake\": true", output);
    }

    [Fact]
    public async Task Tail_TextOutput_ShowsMatchMarkers()
    {
        var evt = new DirectAgentEvent(
            EventId: "evt-1",
            CreatedAt: DateTimeOffset.UtcNow,
            Sender: "user-1",
            ReplyContext: null,
            Source: new SourceContext("den-host", null, null, null, null),
            Target: new TargetWork(null, null, "den-host-01", null));
        var outcome = new EventMatchOutcome(
            EventId: "evt-1", IsForUs: true, Reason: "pool_member_match",
            IntendedAction: "wake", MigrationDiffNote: "delta");
        var result = new ChannelsEventReadResult(
            new DirectAgentEventPage(new[] { evt }, null),
            new[] { outcome },
            EndpointImplemented: true);
        var cmd = new EventsCommand(new FixedReader(result), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("[FOR-US]", output);
        Assert.Contains("evt-1", output);
    }

    [Fact]
    public async Task UnknownSubcommand_Exits2()
    {
        var cmd = new EventsCommand(new FixedReader(MakeEmptyResult(true)), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "bogus" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    private static ChannelsEventReadResult MakeEmptyResult(bool implemented) => new(
        new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null),
        Array.Empty<EventMatchOutcome>(),
        implemented);

    private sealed class FixedReader : IChannelsEventReader
    {
        private readonly ChannelsEventReadResult _result;
        public FixedReader(ChannelsEventReadResult result) { _result = result; }
        public Task<ChannelsEventReadResult> ReadPageAsync(int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }
}
