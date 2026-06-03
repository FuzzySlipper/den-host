using DenHost.Channels;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class ChannelsEventReaderTests : IDisposable
{
    private readonly string _tempDir;

    public ChannelsEventReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private (ChannelsEventReader Reader, FakeChannelsClient Channels, EventCursorStore Store) Build(
        ChannelsEventPage? firstPage = null,
        HttpRequestException? throws = null)
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        var store = new EventCursorStore(runtime, NullLogger<EventCursorStore>.Instance);
        var channels = new FakeChannelsClient(firstPage, throws);
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "den-host-01",
            Host = "h-1",
            ManagedRoles = new[] { "coder" },
            ManagedCapabilities = Array.Empty<string>(),
        };
        var reader = new ChannelsEventReader(
            channels, store, identity, NullLogger<ChannelsEventReader>.Instance);
        return (reader, channels, store);
    }

    [Fact]
    public async Task ReadPageAsync_PassesQueryToChannels()
    {
        var (reader, channels, _) = Build();
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), 100, HasMore: false, EndpointImplemented: true);

        var query = new ChannelsEventReadQuery(ChannelId: 42, ProjectId: null, AfterId: null, PageSize: 25);
        await reader.ReadPageAsync(query, CancellationToken.None);

        Assert.Equal(42, channels.LastChannelIdArg);
        Assert.Null(channels.LastProjectIdArg);
        Assert.Equal(25, channels.LastLimitArg);
    }

    [Fact]
    public async Task ReadPageAsync_UsesStoredCursorWhenQueryAfterIdIsNull()
    {
        var (reader, channels, store) = Build();
        await store.WriteAsync(7777, CancellationToken.None);
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), 7799, HasMore: false, EndpointImplemented: true);

        var query = new ChannelsEventReadQuery(ChannelId: 1, ProjectId: null, AfterId: null, PageSize: 50);
        await reader.ReadPageAsync(query, CancellationToken.None);

        Assert.Equal(7777, channels.LastAfterIdArg);
    }

    [Fact]
    public async Task ReadPageAsync_QueryAfterIdOverridesStoredCursor()
    {
        var (reader, channels, store) = Build();
        await store.WriteAsync(100, CancellationToken.None);
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: true);

        var query = new ChannelsEventReadQuery(ChannelId: 1, ProjectId: null, AfterId: 50, PageSize: 50);
        await reader.ReadPageAsync(query, CancellationToken.None);

        Assert.Equal(50, channels.LastAfterIdArg);
    }

    [Fact]
    public async Task ReadPageAsync_AdvancesCursorFromResponse()
    {
        var (reader, channels, store) = Build();
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), 9001, HasMore: true, EndpointImplemented: true);

        var query = new ChannelsEventReadQuery(1, null, null, 50);
        await reader.ReadPageAsync(query, CancellationToken.None);

        Assert.Equal(9001, await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_NullNextAfterId_DoesNotClearExistingCursor()
    {
        // Null next-after-id from Channels is treated as "no advance"
        // (e.g., the page is the tail of the stream).
        var (reader, channels, store) = Build();
        await store.WriteAsync(500, CancellationToken.None);
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), NextAfterId: null, HasMore: false, EndpointImplemented: true);

        var query = new ChannelsEventReadQuery(1, null, null, 50);
        await reader.ReadPageAsync(query, CancellationToken.None);

        Assert.Equal(500, await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_MatchesOnlyWakeEvents()
    {
        var wake = new ChannelsEvent(
            EventId: 1, ChannelId: 1, MessageKind: "human_text", SenderType: "user",
            SenderIdentity: "u", SourceKind: "wake_event", SourceId: "direct-agent-message:1:h:abc",
            SourceProjectId: "den-host", TargetProjectId: null, TargetTaskId: null,
            AssignmentId: null, WorkerRunId: null, WorkerRole: null,
            ProfileIdentity: null, PoolMemberId: "den-host-01",
            AgentInstanceId: null, SessionOwnerId: null, SessionId: null,
            DeliveryRequestId: null, DedupeKey: null, DeepLink: null,
            Summary: "wake", Body: "go", CreatedAt: DateTimeOffset.UtcNow);
        var nonWake = wake with { EventId = 2, SourceKind = "chat_message" };
        channels_with_page(wake, nonWake, out var channels, out var reader, out _);

        var result = await reader.ReadPageAsync(new ChannelsEventReadQuery(1, null, null, 50), CancellationToken.None);

        Assert.Equal(2, result.Page.Items.Count);
        Assert.Single(result.Outcomes);
        Assert.Equal(1, result.Outcomes[0].EventId);
    }

    [Fact]
    public async Task ReadPageAsync_MatchesEventAgainstIdentity()
    {
        var wake = new ChannelsEvent(
            EventId: 1, ChannelId: 1, MessageKind: "human_text", SenderType: "user",
            SenderIdentity: "u", SourceKind: "wake_event", SourceId: "direct-agent-message:1:h:abc",
            SourceProjectId: "den-host", TargetProjectId: null, TargetTaskId: null,
            AssignmentId: null, WorkerRunId: null, WorkerRole: null,
            ProfileIdentity: null, PoolMemberId: "den-host-01",
            AgentInstanceId: null, SessionOwnerId: null, SessionId: null,
            DeliveryRequestId: null, DedupeKey: null, DeepLink: null,
            Summary: "wake", Body: "go", CreatedAt: DateTimeOffset.UtcNow);
        channels_with_page(wake, out var channels, out var reader, out _);

        var result = await reader.ReadPageAsync(new ChannelsEventReadQuery(1, null, null, 50), CancellationToken.None);

        Assert.True(result.EndpointImplemented);
        Assert.Single(result.Outcomes);
        Assert.True(result.Outcomes[0].IsForUs);
        Assert.Equal("pool_member_match", result.Outcomes[0].Reason);
    }

    [Fact]
    public async Task ReadPageAsync_OnHttpError_ReportsEndpointNotImplemented()
    {
        var (reader, _, _) = Build(throws: new HttpRequestException("Connection refused"));
        var result = await reader.ReadPageAsync(new ChannelsEventReadQuery(1, null, null, 50), CancellationToken.None);

        Assert.False(result.EndpointImplemented);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public async Task ReadPageAsync_OnHttpError_DoesNotAdvanceCursor()
    {
        var (reader, _, store) = Build(throws: new HttpRequestException("Connection refused"));
        await store.WriteAsync(4242, CancellationToken.None);

        await reader.ReadPageAsync(new ChannelsEventReadQuery(1, null, null, 50), CancellationToken.None);

        Assert.Equal(4242, await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_EmptyPageFromImplementedEndpoint_AdvancesCursor()
    {
        var (reader, channels, store) = Build();
        channels.NextPage = new ChannelsEventPage(Array.Empty<ChannelsEvent>(), 9999, HasMore: false, EndpointImplemented: true);

        var result = await reader.ReadPageAsync(new ChannelsEventReadQuery(1, null, null, 50), CancellationToken.None);

        Assert.True(result.EndpointImplemented);
        Assert.Equal(9999, await store.ReadAsync(CancellationToken.None));
    }

    private static void channels_with_page(ChannelsEvent evt, out FakeChannelsClient channels, out ChannelsEventReader reader, out EventCursorStore store)
    {
        channels_with_page(new[] { evt }, out channels, out reader, out store);
    }

    private static void channels_with_page(ChannelsEvent e1, ChannelsEvent e2, out FakeChannelsClient channels, out ChannelsEventReader reader, out EventCursorStore store)
    {
        channels_with_page(new[] { e1, e2 }, out channels, out reader, out store);
    }

    private static void channels_with_page(IReadOnlyList<ChannelsEvent> items, out FakeChannelsClient channels, out ChannelsEventReader reader, out EventCursorStore store)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var runtime = new RuntimeOptions
            {
                ConfigDir = tempDir,
                RunDir = Path.Combine(tempDir, "run"),
                StateDir = Path.Combine(tempDir, "state"),
                LogDir = Path.Combine(tempDir, "log"),
                QuarantineDir = Path.Combine(tempDir, "quarantine"),
            };
            store = new EventCursorStore(runtime, NullLogger<EventCursorStore>.Instance);
            channels = new FakeChannelsClient(new ChannelsEventPage(items, null, HasMore: false, EndpointImplemented: true), null);
            var identity = new AdapterIdentity
            {
                Kind = "host",
                InstanceId = "den-host-01",
                Host = "h-1",
                ManagedRoles = new[] { "coder" },
                ManagedCapabilities = Array.Empty<string>(),
            };
            reader = new ChannelsEventReader(channels, store, identity, NullLogger<ChannelsEventReader>.Instance);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }

    private sealed class FakeChannelsClient : IChannelsClient
    {
        private readonly ChannelsEventPage? _defaultPage;
        private readonly HttpRequestException? _throws;
        public long? LastChannelIdArg { get; private set; }
        public string? LastProjectIdArg { get; private set; }
        public long? LastAfterIdArg { get; private set; }
        public int LastLimitArg { get; private set; }
        public long? LastGetEventIdArg { get; private set; }
        public ChannelsEventPage? NextPage { get; set; }
        public ChannelsEventReadback? NextReadback { get; set; }

        public FakeChannelsClient(ChannelsEventPage? defaultPage = null, HttpRequestException? throws = null)
        {
            _defaultPage = defaultPage;
            _throws = throws;
        }

        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");

        public Task<ChannelsEventPage> GetDirectAgentEventsAsync(
            long? channelId, string? projectId, long? afterId, int limit, CancellationToken cancellationToken)
        {
            LastChannelIdArg = channelId;
            LastProjectIdArg = projectId;
            LastAfterIdArg = afterId;
            LastLimitArg = limit;
            if (_throws is not null) throw _throws;
            return Task.FromResult(NextPage ?? _defaultPage ?? new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: true));
        }

        public Task<ChannelsEventReadback?> GetDirectAgentEventAsync(long eventId, CancellationToken cancellationToken)
        {
            LastGetEventIdArg = eventId;
            return Task.FromResult(NextReadback);
        }
    }
}
