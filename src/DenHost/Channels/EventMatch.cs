using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Channels;

/// <summary>
/// Outcome of evaluating whether a Channels event is for this host.
/// Computed in shadow mode without launching a worker; the reader
/// logs the outcome and the intended wake decision.
/// </summary>
public sealed record EventMatchOutcome(
    long EventId,
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
/// Decides whether a Channels event is for this host. Pure logic;
/// no IO, no process invocation. The intended wake decision is
/// always a planned action -- the shadow-mode reader never executes it.
///
/// Matching rules (in order, with the first match winning):
///   1. <c>SourceKind == "wake_event"</c> filter is applied by the
///      reader before calling the matcher. Only wake events reach
///      the matcher.
///   2. <c>PoolMemberId</c> matches an identifier we own (the
///      adapter instance id).
///   3. <c>WorkerRole</c> is in our managed roles (case-insensitive).
///   4. <c>AssignmentId</c> or <c>WorkerRunId</c> is addressed; we do
///      not currently have a way to know if we own a given assignment
///      or run from local state, so this match is "structurally
///      present" only and notes that the answer depends on Core.
///   5. Otherwise: no_matching_target.
///
/// Migration diff note: the host matches on generic Den-facing
/// fields (PoolMemberId, WorkerRole, AssignmentId, WorkerRunId);
/// the previous Gateway delivery loop matched on Hermes profile
/// name + session key. This comparison is kept for cutover auditing
/// only; Gateway is decommissioned and the host uses the Channels
/// Direct Delivery / active-work routing green path.
/// </summary>
public static class EventMatcher
{
    public static EventMatchOutcome Match(ChannelsEvent evt, AdapterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(identity);

        // Rule 2: pool member id matches our adapter instance id.
        if (!string.IsNullOrEmpty(evt.PoolMemberId)
            && evt.PoolMemberId == identity.InstanceId)
        {
            return new EventMatchOutcome(
                EventId: evt.EventId,
                IsForUs: true,
                Reason: "pool_member_match",
                IntendedAction: "wake",
                MigrationDiffNote:
                    "Host matches on pool_member_id == adapter instance id; " +
                    "previous Gateway delivery loop matched on Hermes profile name. " +
                    "Direct Delivery uses the generic pool_member_id; Gateway is decommissioned.");
        }

        // Rule 3: role is in our managed roles.
        if (!string.IsNullOrEmpty(evt.WorkerRole)
            && identity.ManagedRoles.Contains(evt.WorkerRole, StringComparer.OrdinalIgnoreCase))
        {
            return new EventMatchOutcome(
                EventId: evt.EventId,
                IsForUs: true,
                Reason: "role_match",
                IntendedAction: "wake",
                MigrationDiffNote:
                    "Host matches on generic role name (no Hermes profile); " +
                    "previous Gateway delivery loop would match on Hermes profile " +
                    "name first, then role as a fallback. Gateway is decommissioned.");
        }

        // Rule 4: assignment or run id present; we cannot decide
        // locally whether we own it (Core is the source of truth),
        // so we report "structurally present" and note the gap.
        if (!string.IsNullOrEmpty(evt.AssignmentId) || !string.IsNullOrEmpty(evt.WorkerRunId))
        {
            var why = !string.IsNullOrEmpty(evt.AssignmentId)
                ? $"assignment_id={evt.AssignmentId} present but ownership check requires Core; host held the event for diagnostic visibility."
                : $"worker_run_id={evt.WorkerRunId} present but ownership check requires Core; host held the event for diagnostic visibility.";
            return new EventMatchOutcome(
                EventId: evt.EventId,
                IsForUs: true,
                Reason: "assignment_or_run_present",
                IntendedAction: "hold_for_core_check",
                MigrationDiffNote: why);
        }

        // Rule 5: no match.
        var whyDetail = (evt.PoolMemberId, evt.WorkerRole, evt.AssignmentId, evt.WorkerRunId) switch
        {
            ({ } pmi, _, _, _) => $"pool_member_id={pmi} did not match any locally owned pool member",
            (_, { } role, _, _) => $"worker_role={role} is not in our managed roles [{string.Join(",", identity.ManagedRoles)}]",
            (_, _, { } aid, _) => $"assignment_id={aid} was not addressed to this host",
            (_, _, _, { } rid) => $"worker_run_id={rid} was not addressed to this host",
            _ => "event has no target work metadata; cannot match",
        };
        return new EventMatchOutcome(
            EventId: evt.EventId,
            IsForUs: false,
            Reason: "no_matching_target",
            IntendedAction: null,
            MigrationDiffNote: whyDetail);
    }
}
