namespace DenHost.Clients;

/// <summary>
/// Health state of this host's adapter binding as known locally.
/// "registered" / "stale" / "endpoint-missing" / "unknown" are mutually exclusive.
/// </summary>
public enum AdapterBindingState
{
    /// <summary>
    /// The binding was never probed (e.g. <c>den-host health</c> run before any heartbeat,
    /// or in a configuration that has no BindingPath and no probe has been attempted).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The most recent probe against Core succeeded; the binding is fresh.
    /// </summary>
    Registered = 1,

    /// <summary>
    /// A previous probe succeeded but the most recent probe failed (Core unreachable
    /// or returned an error). The LastSeen field still points at the last
    /// successful registration; the LastError field captures the most recent failure.
    /// </summary>
    Stale = 2,

    /// <summary>
    /// The Core binding endpoint is not configured, so registration is impossible.
    /// Per den-host task #1915 AC, this is a blocker; the host writes blocker
    /// evidence under <c>RuntimeOptions.StateDir</c> instead of inventing a
    /// Host-local truth table.
    /// </summary>
    EndpointMissing = 3,
}

/// <summary>
/// Snapshot of the local view of this host's adapter binding.
/// <see cref="State"/> is the source of truth for the host's diagnostic output;
/// <see cref="BlockerEvidencePath"/> is populated only when
/// <see cref="State"/> is <see cref="AdapterBindingState.EndpointMissing"/>.
/// </summary>
public sealed record AdapterBindingHealth(
    AdapterBindingState State,
    DateTimeOffset? LastSeen,
    string? LastError,
    string? BlockerEvidencePath)
{
    public static AdapterBindingHealth Unknown() =>
        new(AdapterBindingState.Unknown, null, null, null);

    public static AdapterBindingHealth Registered(DateTimeOffset lastSeen) =>
        new(AdapterBindingState.Registered, lastSeen, null, null);

    public static AdapterBindingHealth Stale(DateTimeOffset? lastSeen, string lastError) =>
        new(AdapterBindingState.Stale, lastSeen, lastError, null);

    public static AdapterBindingHealth EndpointMissing(string reason, string? blockerEvidencePath) =>
        new(AdapterBindingState.EndpointMissing, null, reason, blockerEvidencePath);

    /// <summary>
    /// Returns true if the binding is fresh enough to consider the adapter
    /// "live" from Core's point of view. A registered binding is fresh; a
    /// stale or missing binding is not.
    /// </summary>
    public bool IsFresh => State == AdapterBindingState.Registered;
}
