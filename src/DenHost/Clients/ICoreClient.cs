namespace DenHost.Clients;

/// <summary>
/// Request body for adapter binding registration/heartbeat.
/// Uses generic Den-facing fields only. No Hermes profile, no
/// harness-specific session/process/path data.
/// </summary>
/// <param name="AdapterKind">
/// The configured adapter kind, e.g. "host".
/// </param>
/// <param name="AdapterInstanceId">
/// Stable, globally-unique-per-host identifier for this adapter.
/// </param>
/// <param name="Host">
/// Human-readable machine identifier.
/// </param>
/// <param name="ManagedRoles">
/// Roles this adapter claims to be able to satisfy.
/// </param>
/// <param name="ManagedCapabilities">
/// Generic capabilities this adapter claims to be able to satisfy.
/// </param>
/// <param name="ProjectId">
/// Optional project id scope for the binding. Null means unscoped.
/// </param>
public sealed record AdapterBindingRequest(
    string AdapterKind,
    string AdapterInstanceId,
    string Host,
    IReadOnlyList<string> ManagedRoles,
    IReadOnlyList<string> ManagedCapabilities,
    string? ProjectId = null);

/// <summary>
/// Readback of a registered adapter binding from Core.
/// </summary>
public sealed record AdapterBindingSnapshot(
    string AdapterInstanceId,
    string AdapterKind,
    string Host,
    IReadOnlyList<string> ManagedRoles,
    IReadOnlyList<string> ManagedCapabilities,
    DateTimeOffset LastSeen,
    string? State);

/// <summary>
/// HTTP client contract for the Core endpoint.
/// </summary>
public interface ICoreClient
{
    /// <summary>
    /// Probes the Core health endpoint and returns a structured
    /// reachability result. Never throws on connectivity failure.
    /// </summary>
    Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Registers or heartbeats this adapter's binding with Core.
    /// Returns the readback snapshot from Core, or throws
    /// <see cref="NotSupportedException"/> if no binding path is
    /// configured (den-host task #1915 documents the fallback).
    /// </summary>
    Task<AdapterBindingSnapshot> RegisterAdapterBindingAsync(
        AdapterBindingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the current binding state for this adapter from Core.
    /// Returns null if no binding is recorded.
    /// </summary>
    Task<AdapterBindingSnapshot?> GetAdapterBindingAsync(
        string adapterInstanceId,
        CancellationToken cancellationToken);
}
