using DenHost.Channels;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Harness;
using DenHost.Host;
using DenHost.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class AgentWorkLifecycleEmitterTests
{
    [Fact]
    public async Task EmitDirectAgentRuntimeReceivedAsync_WritesChannelsLifecycleRequest()
    {
        var channels = new RecordingChannelsClient();
        var emitter = BuildEmitter(channels, channelId: 672);
        var evt = new ChannelsEvent(
            EventId: 2517,
            ChannelId: 672,
            MessageKind: "human_text",
            SenderType: "agent",
            SenderIdentity: "den-mcp-runner",
            SourceKind: "wake_event",
            SourceId: "direct-agent-message:672:spawned-validator:abc123",
            SourceProjectId: "den-channels",
            TargetProjectId: "den-channels",
            TargetTaskId: 1967,
            AssignmentId: "204",
            WorkerRunId: "piw_1967_validate",
            WorkerRole: "validator",
            ProfileIdentity: "spawned-validator",
            PoolMemberId: "pool-validator-03",
            AgentInstanceId: "hermes:den-k8:spawned-validator",
            SessionOwnerId: "pool-validator-03",
            SessionId: "session-1",
            DeliveryRequestId: null,
            DedupeKey: "wake-2517",
            DeepLink: null,
            Summary: "wake validator",
            Body: "validate",
            CreatedAt: DateTimeOffset.UtcNow);
        var outcome = new EventMatchOutcome(evt.EventId, true, "pool_member_match", "wake", "shadow only");

        await emitter.EmitDirectAgentRuntimeReceivedAsync(evt, outcome, CancellationToken.None);

        var request = Assert.Single(channels.LifecycleRequests);
        Assert.Equal(672, request.ChannelId);
        Assert.Equal("spawned-validator", request.AgentIdentity);
        Assert.Equal("pool-validator-03", request.PoolMemberId);
        Assert.Equal("runtime_received", request.EventType);
        Assert.Equal(1967, request.TaskId);
        Assert.Equal("204", request.AssignmentId);
        Assert.Equal("piw_1967_validate", request.WorkerRunId);
        Assert.Equal("validator", request.WorkerRole);
        Assert.Equal("2517", request.DirectAgentEventId);
        Assert.Equal("den-host-test", request.HostId);
        Assert.Equal("den-host:den-host-test:runtime_received:2517", request.DedupeKey);
        Assert.Contains("pool_member_match", request.MetadataJson);
    }

    [Fact]
    public async Task EmitRunLifecycleAsync_WritesHeartbeatWithWorkerCorrelation()
    {
        var channels = new RecordingChannelsClient();
        var emitter = BuildEmitter(channels, channelId: 604);
        var record = new LocalRunRecord(
            LocalRunId: "local-1",
            WorkerRunId: "piw_worker_1",
            AssignmentId: 200,
            TaskId: 1957,
            Role: "coder",
            ProfileIdentity: "spawned-coder",
            PoolMemberId: "pool-coder-01",
            HarnessKind: HarnessModuleKind.Hermes,
            HarnessModuleName: "hermes",
            ProcessId: 12345,
            LogFilePath: "/tmp/run.log",
            StartedAt: DateTimeOffset.UtcNow,
            State: LocalRunState.Running,
            RunDir: "/tmp/run");

        await emitter.EmitRunLifecycleAsync(record, "heartbeat", "test heartbeat", CancellationToken.None);

        var request = Assert.Single(channels.LifecycleRequests);
        Assert.Equal(604, request.ChannelId);
        Assert.Equal("pool-coder-01", request.AgentIdentity);
        Assert.Equal("heartbeat", request.EventType);
        Assert.Equal(1957, request.TaskId);
        Assert.Equal("200", request.AssignmentId);
        Assert.Equal("piw_worker_1", request.WorkerRunId);
        Assert.Equal("coder", request.WorkerRole);
        Assert.Equal("den-host-test", request.HostId);
        Assert.Equal(12345, request.ProcessId);
        Assert.Contains("local-1", request.DedupeKey);
    }

    private static AgentWorkLifecycleEmitter BuildEmitter(RecordingChannelsClient channels, long channelId)
    {
        var options = new ChannelsOptions
        {
            BaseUrl = "http://channels.test",
            EventsListChannelId = channelId,
        };
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "den-host-test",
            Host = "den-k8",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        return new AgentWorkLifecycleEmitter(
            channels,
            options,
            identity,
            NullLogger<AgentWorkLifecycleEmitter>.Instance);
    }

    private sealed class RecordingChannelsClient : IChannelsClient
    {
        public List<AgentWorkLifecycleWriteRequest> LifecycleRequests { get; } = new();

        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProbeResult.Ok(200, 1));

        public Task<ChannelsEventPage> GetDirectAgentEventsAsync(
            long? channelId,
            string? projectId,
            long? afterId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, false, true));

        public Task<ChannelsEventReadback?> GetDirectAgentEventAsync(long eventId, CancellationToken cancellationToken) =>
            Task.FromResult<ChannelsEventReadback?>(null);

        public Task<AgentWorkLifecycleWriteResult> PostAgentWorkLifecycleEventAsync(
            AgentWorkLifecycleWriteRequest request,
            CancellationToken cancellationToken)
        {
            LifecycleRequests.Add(request);
            return Task.FromResult(new AgentWorkLifecycleWriteResult(true, 201, true, "1", null));
        }
    }
}
