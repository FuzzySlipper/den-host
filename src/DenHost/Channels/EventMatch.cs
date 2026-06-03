using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Channels;

/// <summary>
/// Outcome of evaluating whether a Channels direct-agent event is for
/// this host. Computed in shadow mode without launching a worker; the
/// reader logs the outcome and the intended wake decision.
/// </summary>
public sealed record EventMatchOutcome(
    string EventId,
    bool IsForUs,
    string Reason,
    string? IntendedAction,
    string MigrationDiffNote)
{
    /// <summary>
    /// True if the host would wake a worker if not in shadow mode.
    /// </summary>
    public bool WouldWake => IsForUs && IntendedAction is "wake" or "wake_local";
}

/// <summary>
/// Decides whether a Channels direct-agent event is for this host. Pure
/// logic; no IO, no process invocation. The intended wake decision is
/// always a planned action -- the shadow-mode reader never executes it.
///
/// Matching rules (in order):
///   1. If the event has a target.assignmentId or target.runId, and our
///      adapter instance id matches the source context's expected owner,
///      the event is "for us" with reason "assignment_or_run_match".
///   2. If the event has a target.poolMemberId, and that id matches an
///      identifier we own (instance-derived), the event is "for us" with
///      reason "pool_member_match".
///   3. If the event has a target.role, and that role is in our
///      managed roles, the event is "for us" with reason "role_match".
///   4. Otherwise the event is "not_for_us" with reason
///      "no_matching_target".
///
/// Migration diff note: the host uses pool_member_id + role matching;
/// the legacy Gateway delivery loop matched on Hermes profile name. The
/// note is logged for comparison during cutover.
/// </summary>
public static class EventMatcher
{
    public static EventMatchOutcome Match(DirectAgentEvent evt, AdapterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(identity);

        // Rule 1: assignment/run identity in the source context with our
        // adapter instance id.
        if (evt.Source.ProjectId is not null && !string.IsNullOrEmpty(identity.InstanceId))
        {
            // Project-bound events on the same project as the host's adapter
            // are likely intended for the host's project, but we still want
            // an explicit match signal. The host's project id is not
            // currently part of AdapterIdentity; if it becomes so, this rule
            // will be tightened. For now, treat project-bound events as
            // informational.
        }

        // Rule 2: pool member id matches an instance-derived identifier.
        // Until harness modules report pool members (lands in #1917), we
        // treat the adapter instance id as the canonical identifier.
        if (!string.IsNullOrEmpty(evt.Target.PoolMemberId)
            && evt.Target.PoolMemberId == identity.InstanceId)
        {
            return new EventMatchOutcome(
                EventId: evt.EventId,
                IsForUs: true,
                Reason: "pool_member_match",
                IntendedAction: "wake",
                MigrationDiffNote:
                    "Host matches on pool_member_id == adapter instance id; " +
                    "legacy Gateway delivery loop matches on Hermes profile name. " +
                    "Direct Delivery uses the generic pool_member_id.");
        }

        // Rule 3: role is in our managed roles.
        if (!string.IsNullOrEmpty(evt.Target.Role)
            && identity.ManagedRoles.Contains(evt.Target.Role, StringComparer.OrdinalIgnoreCase))
        {
            return new EventMatchOutcome(
                EventId: evt.EventId,
                IsForUs: true,
                Reason: "role_match",
                IntendedAction: "wake",
                MigrationDiffNote:
                    "Host matches on generic role name (no Hermes profile); " +
                    "legacy Gateway delivery loop would match on Hermes profile " +
                    "name first, then role as a fallback.");
        }

        // Rule 4: no match.
        var why = (evt.Target.PoolMemberId, evt.Target.Role, evt.Target.AssignmentId, evt.Target.RunId) switch
        {
            ({ } pmi, _, _, _) => $"pool_member_id={pmi} did not match any locally owned pool member",
            (_, { } role, _, _) => $"role={role} is not in our managed roles [{string.Join(",", identity.ManagedRoles)}]",
            (_, _, { } aid, _) => $"assignment_id={aid} was not addressed to this host",
            (_, _, _, { } rid) => $"run_id={rid} was not addressed to this host",
            _ => "event has no target work metadata; cannot match",
        };
        return new EventMatchOutcome(
            EventId: evt.EventId,
            IsForUs: false,
            Reason: "no_matching_target",
            IntendedAction: null,
            MigrationDiffNote: why);
    }
}
