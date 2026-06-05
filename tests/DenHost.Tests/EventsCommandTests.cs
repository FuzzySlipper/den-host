using DenHost.Channels;
using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Configuration;

namespace DenHost.Tests;

public class EventsCommandTests
{
    [Fact]
    public async Task ExecuteAsync_NoSubcommand_PrintsHelp()
    {
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, new FakeChannelsClient());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Usage: den-host events", ((BufferingCliHost)context.Host).StandardOut);
    }

    [Fact]
    public async Task Tail_ImplementedEndpoint_Exits0()
    {
        var reader = new FakeReader(MakeEmptyResult(true));
        var channels = new FakeChannelsClient();
        var cmd = BuildCommand(reader, null, channels, channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Tail_PropagatesConfigChannelIdToClient()
    {
        // Use a real reader so the client is actually called and the
        // config-default channel id propagates through the query.
        var tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var runtime = new DenHost.Configuration.RuntimeOptions
            {
                ConfigDir = tempDir,
                RunDir = Path.Combine(tempDir, "run"),
                StateDir = Path.Combine(tempDir, "state"),
                LogDir = Path.Combine(tempDir, "log"),
                QuarantineDir = Path.Combine(tempDir, "quarantine"),
            };
            var store = new DenHost.Channels.EventCursorStore(runtime, Microsoft.Extensions.Logging.Abstractions.NullLogger<DenHost.Channels.EventCursorStore>.Instance);
            var identity = new DenHost.Host.AdapterIdentity
            {
                Kind = "host",
                InstanceId = "den-host-01",
                Host = "h-1",
                ManagedRoles = Array.Empty<string>(),
                ManagedCapabilities = Array.Empty<string>(),
            };
            var channels = new FakeChannelsClient();
            var reader = new DenHost.Channels.ChannelsEventReader(
                channels, store, identity, new RecordingLifecycleEmitter(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DenHost.Channels.ChannelsEventReader>.Instance);
            var cmd = BuildCommand(reader, store, channels, channelId: 42, projectId: null);
            var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

            var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Equal(42, channels.LastChannelIdArg);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Tail_MissingEndpoint_Exits5()
    {
        var reader = new FakeReader(MakeEmptyResult(false));
        var cmd = BuildCommand(reader, null, new FakeChannelsClient(), channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(5, exit);
    }

    [Fact]
    public async Task Tail_WithoutChannelIdOrProjectId_Exits2()
    {
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, new FakeChannelsClient(), channelId: null, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Tail_JsonOutput_ContainsKey()
    {
        var evt = MakeEvent();
        var outcome = new EventMatchOutcome(evt.EventId, true, "pool_member_match", "wake", "delta");
        var result = new ChannelsEventReadResult(
            new ChannelsEventPage(new[] { evt }, null, HasMore: false, EndpointImplemented: true),
            new[] { outcome },
            EndpointImplemented: true);
        var reader = new FakeReader(result);
        var cmd = BuildCommand(reader, null, new FakeChannelsClient(), channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "tail", "--json" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("\"endpointImplemented\": true", output);
        Assert.Contains("\"isForUs\": true", output);
        Assert.Contains("\"wouldWake\": true", output);
    }

    [Fact]
    public async Task Tail_TextOutput_ShowsMatchMarkers()
    {
        var evt = MakeEvent();
        var outcome = new EventMatchOutcome(evt.EventId, true, "pool_member_match", "wake", "delta");
        var result = new ChannelsEventReadResult(
            new ChannelsEventPage(new[] { evt }, null, HasMore: false, EndpointImplemented: true),
            new[] { outcome },
            EndpointImplemented: true);
        var cmd = BuildCommand(new FakeReader(result), null, new FakeChannelsClient(), channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "tail" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("[FOR-US]", output);
    }

    [Fact]
    public async Task Get_FoundEvent_Exits0()
    {
        var readback = MakeReadback(eventId: 7);
        var channels = new FakeChannelsClient(readback: readback);
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, channels, channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "get", "7" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(7, channels.LastGetEventIdArg);
        Assert.Contains("Channels event 7", ((BufferingCliHost)context.Host).StandardOut);
    }

    [Fact]
    public async Task Get_NotFound_Exits4()
    {
        var channels = new FakeChannelsClient(readback: null);
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, channels, channelId: 42, projectId: null);
        var context = new CliContext(new BufferingCliHost(), new[] { "get", "999" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(4, exit);
    }

    [Fact]
    public async Task Get_MissingEventIdArg_Exits2()
    {
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, new FakeChannelsClient());
        var context = new CliContext(new BufferingCliHost(), new[] { "get" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Get_BadEventIdArg_Exits2()
    {
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, new FakeChannelsClient());
        var context = new CliContext(new BufferingCliHost(), new[] { "get", "not-a-number" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task UnknownSubcommand_Exits2()
    {
        var cmd = BuildCommand(new FakeReader(MakeEmptyResult(true)), null, new FakeChannelsClient());
        var context = new CliContext(new BufferingCliHost(), new[] { "bogus" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    private static EventsCommand BuildCommand(
        IChannelsEventReader reader,
        EventCursorStore? _,
        IChannelsClient channels,
        long? channelId = 42,
        string? projectId = null)
    {
        var options = new ChannelsOptions
        {
            BaseUrl = "http://127.0.0.1:18082",
            HealthPath = "/healthz",
            EventsListPath = "/api/direct-agent-events",
            EventsListChannelId = channelId,
            EventsListProjectId = projectId,
            DirectAgentEventPath = "/api/direct-agent-events",
            TimeoutMs = 1000,
        };
        return new EventsCommand(reader, channels, options, new BufferingCliHost());
    }

    private static ChannelsEventReadResult MakeEmptyResult(bool implemented) =>
        new(new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: implemented),
            Array.Empty<EventMatchOutcome>(), implemented);

    private static ChannelsEvent MakeEvent(long eventId = 1) => new(
        EventId: eventId, ChannelId: 1, MessageKind: "human_text", SenderType: "user",
        SenderIdentity: "u", SourceKind: "wake_event", SourceId: "direct-agent-message:1:h:abc",
        SourceProjectId: "den-host", TargetProjectId: null, TargetTaskId: null,
        AssignmentId: null, WorkerRunId: null, WorkerRole: null,
        ProfileIdentity: null, PoolMemberId: "den-host-01",
        AgentInstanceId: null, SessionOwnerId: null, SessionId: null,
        DeliveryRequestId: null, DedupeKey: null, DeepLink: null,
        Summary: "wake", Body: "go", CreatedAt: DateTimeOffset.UtcNow);

    private static ChannelsEventReadback MakeReadback(long eventId) => new(
        EventId: eventId, ChannelId: 1, RequestId: "direct-agent-message:1:h:abc",
        MessageKind: "human_text", SenderType: "user", SenderIdentity: "u",
        MemberIdentity: "h", WakePolicy: "claim",
        SourceKind: "wake_event", SourceProjectId: "den-host",
        TargetProjectId: null, TargetTaskId: null,
        AssignmentId: null, WorkerRunId: null, WorkerRole: null,
        ProfileIdentity: null, PoolMemberId: "den-host-01",
        AgentInstanceId: null, SessionOwnerId: null, SessionId: null,
        Summary: "wake", Body: "go",
        DeliveryStatus: "recorded", ClaimStatus: "unclaimed", CompletionStatus: "pending",
        CreatedAt: DateTimeOffset.UtcNow);

    private sealed class RecordingLifecycleEmitter : IAgentWorkLifecycleEmitter
    {
        public Task EmitDirectAgentRuntimeReceivedAsync(ChannelsEvent evt, EventMatchOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EmitRunLifecycleAsync(DenHost.Worker.LocalRunRecord record, string eventType, string stateReason, CancellationToken cancellationToken, string? summary = null, string? dedupeSuffix = null) => Task.CompletedTask;
    }

    private sealed class FakeReader : IChannelsEventReader
    {
        private readonly ChannelsEventReadResult _result;
        public FakeReader(ChannelsEventReadResult result) { _result = result; }
        public Task<ChannelsEventReadResult> ReadPageAsync(ChannelsEventReadQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    private sealed class FakeChannelsClient : IChannelsClient
    {
        private readonly ChannelsEventReadback? _readback;
        public long? LastChannelIdArg { get; private set; }
        public long? LastGetEventIdArg { get; private set; }
        public FakeChannelsClient(ChannelsEventReadback? readback = null) { _readback = readback; }

        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");

        public Task<ChannelsEventPage> GetDirectAgentEventsAsync(
            long? channelId, string? projectId, long? afterId, int limit, CancellationToken cancellationToken)
        {
            LastChannelIdArg = channelId;
            return Task.FromResult(new ChannelsEventPage(
                Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: true));
        }

        public Task<ChannelsEventReadback?> GetDirectAgentEventAsync(long eventId, CancellationToken cancellationToken)
        {
            LastGetEventIdArg = eventId;
            return Task.FromResult(_readback);
        }

        public Task<AgentWorkLifecycleWriteResult> PostAgentWorkLifecycleEventAsync(
            AgentWorkLifecycleWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentWorkLifecycleWriteResult(true, 201, true, "1", null));
    }
}
