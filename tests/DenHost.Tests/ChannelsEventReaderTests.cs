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

    private (ChannelsEventReader Reader, FakeChannelsClient Channels, EventCursorStore Store, AdapterIdentity Identity) Build(
        DirectAgentEventPage? firstPage = null,
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
        return (reader, channels, store, identity);
    }

    [Fact]
    public async Task ReadPageAsync_PassesStoredCursorToChannels()
    {
        var (reader, channels, store, _) = Build();
        await store.WriteAsync("cursor-from-prior-run", CancellationToken.None);
        channels.NextPage = new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), "next-cursor");

        await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.Equal("cursor-from-prior-run", channels.LastCursorArg);
    }

    [Fact]
    public async Task ReadPageAsync_AdvancesCursorFromResponse()
    {
        var first = new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), "next-cursor-xyz");
        var (reader, _, store, _) = Build(first);

        await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.Equal("next-cursor-xyz", await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_NoNextCursor_DoesNotClearExisting()
    {
        // The current implementation only writes the cursor when NextCursor is non-null.
        // A null NextCursor from Channels is treated as "no advance" (e.g., the
        // page is the tail of the stream).
        var first = new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null);
        var (reader, _, store, _) = Build(first);
        await store.WriteAsync("preexisting", CancellationToken.None);

        await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.Equal("preexisting", await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_MatchesEventAgainstIdentity()
    {
        var evt = new DirectAgentEvent(
            EventId: "evt-1",
            CreatedAt: DateTimeOffset.UtcNow,
            Sender: "user-1",
            ReplyContext: null,
            Source: new SourceContext("den-host", null, null, null, null),
            Target: new TargetWork(null, null, "den-host-01", null));
        var first = new DirectAgentEventPage(new[] { evt }, "next");
        var (reader, _, _, _) = Build(first);

        var result = await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.True(result.EndpointImplemented);
        Assert.Single(result.Outcomes);
        Assert.True(result.Outcomes[0].IsForUs);
        Assert.Equal("pool_member_match", result.Outcomes[0].Reason);
    }

    [Fact]
    public async Task ReadPageAsync_OnHttpError_ReportsEndpointNotImplemented()
    {
        var (reader, _, _, _) = Build(throws: new HttpRequestException("Connection refused"));
        var result = await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.False(result.EndpointImplemented);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public async Task ReadPageAsync_OnHttpError_DoesNotAdvanceCursor()
    {
        var (reader, _, store, _) = Build(throws: new HttpRequestException("Connection refused"));
        await store.WriteAsync("preserved-cursor", CancellationToken.None);

        await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.Equal("preserved-cursor", await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadPageAsync_EmptyPageFromImplementedEndpoint_AdvancesCursor()
    {
        var first = new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), "tickled-cursor");
        var (reader, _, store, _) = Build(first);

        var result = await reader.ReadPageAsync(50, CancellationToken.None);

        Assert.True(result.EndpointImplemented);
        Assert.Equal("tickled-cursor", await store.ReadAsync(CancellationToken.None));
    }

    private sealed class FakeChannelsClient : IChannelsClient
    {
        private readonly DirectAgentEventPage? _defaultPage;
        private readonly HttpRequestException? _throws;
        public string? LastCursorArg { get; private set; }
        public int LastLimitArg { get; private set; }
        public DirectAgentEventPage? NextPage { get; set; }

        public FakeChannelsClient(DirectAgentEventPage? defaultPage = null, HttpRequestException? throws = null)
        {
            _defaultPage = defaultPage;
            _throws = throws;
        }

        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");

        public Task<DirectAgentEventPage> GetDirectAgentEventsAsync(string? cursor, int limit, CancellationToken cancellationToken)
        {
            LastCursorArg = cursor;
            LastLimitArg = limit;
            if (_throws is not null)
            {
                throw _throws;
            }
            return Task.FromResult(NextPage ?? _defaultPage ?? new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null));
        }
    }
}
