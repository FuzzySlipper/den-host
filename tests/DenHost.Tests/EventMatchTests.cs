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

    private static DirectAgentEvent Event(
        string eventId = "evt-1",
        string? poolMemberId = null,
        string? role = null,
        int? assignmentId = null,
        string? runId = null) => new(
        EventId: eventId,
        CreatedAt: DateTimeOffset.UtcNow,
        Sender: "user-1",
        ReplyContext: null,
        Source: new SourceContext("den-host", null, null, null, null),
        Target: new TargetWork(assignmentId, runId, poolMemberId, role));

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
        var outcome = EventMatcher.Match(Event(role: "coder"), Identity("coder", "reviewer"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("role_match", outcome.Reason);
        Assert.Equal("wake", outcome.IntendedAction);
    }

    [Fact]
    public void RoleMismatch_NotForUs()
    {
        var outcome = EventMatcher.Match(Event(role: "validator"), Identity("coder", "reviewer"));
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
    public void AssignmentAddressedButNotPool_NotForUs_AndExplainsWhy()
    {
        var outcome = EventMatcher.Match(Event(assignmentId: 42), Identity("coder"));
        Assert.False(outcome.IsForUs);
        Assert.Contains("assignment_id=42", outcome.MigrationDiffNote);
    }

    [Fact]
    public void PoolMemberTakesPrecedenceOverRole()
    {
        // If both pool member and role match, the pool_member match wins
        // (it's a more specific match). The reason reflects this.
        var outcome = EventMatcher.Match(Event(poolMemberId: "den-host-01", role: "coder"), Identity("coder"));
        Assert.True(outcome.IsForUs);
        Assert.Equal("pool_member_match", outcome.Reason);
    }

    [Fact]
    public void RoleMatch_IsCaseInsensitive()
    {
        var outcome = EventMatcher.Match(Event(role: "CODER"), Identity("coder"));
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
