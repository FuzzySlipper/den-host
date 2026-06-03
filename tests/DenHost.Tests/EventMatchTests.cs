using DenHost.Channels;
using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Tests;

public class EventMatchTests
{
    private static AdapterIdentity Identity(params string[] managedRoles) => new()
    {
        Kind = "host",
        InstanceId = "den-host-01",
        Host = "workstation-01",
        ManagedRoles = managedRoles,
        ManagedCapabilities = Array.Empty<string>(),
    };

    private static ChannelsEvent Event(
        long eventId = 1,
        string? sourceKind = "wake_event",
        string? poolMemberId = null,
        string? workerRole = null,
        string? assignmentId = null,
        string? workerRunId = null) => new(
        EventId: eventId,
        ChannelId: 1,
        MessageKind: "human_text",
        SenderType: "user",
        SenderIdentity: "u",
        SourceKind: sourceKind,
        SourceId: "direct-agent-message:1:h:abc",
        SourceProjectId: "den-host",
        TargetProjectId: "den-host",
        TargetTaskId: null,
        AssignmentId: assignmentId,
        WorkerRunId: workerRunId,
        WorkerRole: workerRole,
        ProfileIdentity: null,
        PoolMemberId: poolMemberId,
        AgentInstanceId: null,
        SessionOwnerId: null,
        SessionId: null,
        DeliveryRequestId: null,
        DedupeKey: null,
        DeepLink: null,
        Summary: "wake",
        Body: "go",
        CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void PoolMemberIdMatch_IsForUsWithWakeIntended()
    {
        var outcome = EventMatcher.Match(Event(poolMemberId: "den-host-01"), Identity("coder", "reviewer"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("pool_member_match", outcome.Reason);
        Assert.Equal("wake", outcome.IntendedAction);
        Assert.True(outcome.WouldWake);
    }

    [Fact]
    public void RoleMatch_IsForUsWithWakeIntended()
    {
        var outcome = EventMatcher.Match(Event(workerRole: "coder"), Identity("coder", "reviewer"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("role_match", outcome.Reason);
        Assert.Equal("wake", outcome.IntendedAction);
    }

    [Fact]
    public void RoleMismatch_NotForUs()
    {
        var outcome = EventMatcher.Match(Event(workerRole: "validator"), Identity("coder", "reviewer"));
        Assert.False(outcome.IsForUs);
        Assert.Equal("no_matching_target", outcome.Reason);
        Assert.Null(outcome.IntendedAction);
    }

    [Fact]
    public void PoolMemberIdMismatch_NotForUs()
    {
        var outcome = EventMatcher.Match(Event(poolMemberId: "different-host"), Identity("coder"));
        Assert.False(outcome.IsForUs);
        Assert.Equal("no_matching_target", outcome.Reason);
        Assert.Contains("did not match any locally owned pool member", outcome.MigrationDiffNote);
    }

    [Fact]
    public void NoTargetMetadata_NotForUs()
    {
        var outcome = EventMatcher.Match(Event(), Identity("coder"));
        Assert.False(outcome.IsForUs);
        Assert.Equal("no_matching_target", outcome.Reason);
        Assert.Contains("no target work metadata", outcome.MigrationDiffNote);
    }

    [Fact]
    public void AssignmentIdPresent_HeldForCoreCheck()
    {
        var outcome = EventMatcher.Match(Event(assignmentId: "42"), Identity("coder"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("assignment_or_run_present", outcome.Reason);
        Assert.Equal("hold_for_core_check", outcome.IntendedAction);
        Assert.False(outcome.WouldWake);
    }

    [Fact]
    public void WorkerRunIdPresent_HeldForCoreCheck()
    {
        var outcome = EventMatcher.Match(Event(workerRunId: "run-7"), Identity("coder"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("assignment_or_run_present", outcome.Reason);
    }

    [Fact]
    public void PoolMemberTakesPrecedenceOverRole()
    {
        var outcome = EventMatcher.Match(Event(poolMemberId: "den-host-01", workerRole: "coder"), Identity("coder"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("pool_member_match", outcome.Reason);
    }

    [Fact]
    public void RoleMatch_IsCaseInsensitive()
    {
        var outcome = EventMatcher.Match(Event(workerRole: "CODER"), Identity("coder"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("role_match", outcome.Reason);
    }

    [Fact]
    public void MigrationDiffNote_AlwaysPresent()
    {
        var outcome = EventMatcher.Match(Event(), Identity());
        Assert.False(string.IsNullOrEmpty(outcome.MigrationDiffNote));
    }
}
