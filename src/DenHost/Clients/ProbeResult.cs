namespace DenHost.Clients;

/// <summary>
/// Outcome of a single reachability probe against a Den endpoint.
/// </summary>
/// <param name="Reachable">
/// True if the endpoint returned a successful response (HTTP 2xx).
/// </param>
/// <param name="StatusCode">
/// HTTP status code if a response was received; null if the call
/// failed before any HTTP response (DNS failure, connection refused, timeout).
/// </param>
/// <param name="LatencyMs">
/// Wall-clock latency in milliseconds. -1 when the call did not complete.
/// </param>
/// <param name="Message">
/// Human-readable diagnostic, e.g. "Connection refused (127.0.0.1:18081)"
/// or "OK".
/// </param>
public sealed record ProbeResult(
    bool Reachable,
    int? StatusCode,
    long LatencyMs,
    string? Message)
{
    public static ProbeResult Ok(int statusCode, long latencyMs) =>
        new(true, statusCode, latencyMs, "OK");

    public static ProbeResult Unreachable(int? statusCode, long latencyMs, string message) =>
        new(false, statusCode, latencyMs, message);
}
