using System.Text.Json;
using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Health;

public static class HealthReportFormatter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Returns a human-readable multi-line rendering of the report.
    /// </summary>
    public static string FormatText(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Den Host");
        sb.AppendLine($"  adapter.kind          = {report.Identity.Kind}");
        sb.AppendLine($"  adapter.instance_id   = {report.Identity.InstanceId}");
        sb.AppendLine($"  adapter.host          = {report.Identity.Host}");
        sb.AppendLine($"  adapter.roles         = [{string.Join(", ", report.Identity.ManagedRoles)}]");
        sb.AppendLine($"  adapter.capabilities  = [{string.Join(", ", report.Identity.ManagedCapabilities)}]");
        sb.AppendLine($"  generated_at          = {report.GeneratedAt:O}");
        sb.AppendLine();

        AppendProbe(sb, "Core", report.Core);
        sb.AppendLine();
        AppendProbe(sb, "Channels", report.Channels);

        if (report.HarnessModules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Harness modules");
            foreach (var module in report.HarnessModules)
            {
                var state = module.Enabled
                    ? (module.Available ? "available" : "unavailable")
                    : "disabled";
                sb.AppendLine($"  - {module.Name} ({module.Kind}) = {state}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Overall: {(report.IsHealthy ? "healthy" : "degraded")}");
        return sb.ToString();
    }

    /// <summary>
    /// Returns a JSON rendering suitable for machine consumption.
    /// </summary>
    public static string FormatJson(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var dto = new
        {
            identity = new
            {
                kind = report.Identity.Kind,
                instanceId = report.Identity.InstanceId,
                host = report.Identity.Host,
                managedRoles = report.Identity.ManagedRoles,
                managedCapabilities = report.Identity.ManagedCapabilities,
            },
            core = ToDto(report.Core),
            channels = ToDto(report.Channels),
            harnessModules = report.HarnessModules.Select(m => new
            {
                name = m.Name,
                kind = m.Kind.ToString(),
                enabled = m.Enabled,
                available = m.Available,
            }),
            generatedAt = report.GeneratedAt,
            isHealthy = report.IsHealthy,
        };

        return JsonSerializer.Serialize(dto, s_jsonOptions);
    }

    private static void AppendProbe(System.Text.StringBuilder sb, string label, ProbeResult probe)
    {
        sb.AppendLine($"{label}");
        sb.AppendLine($"  reachable    = {probe.Reachable.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  status_code  = {(probe.StatusCode?.ToString() ?? "-")}");
        sb.AppendLine($"  latency_ms   = {probe.LatencyMs}");
        sb.AppendLine($"  message      = {(string.IsNullOrEmpty(probe.Message) ? "-" : probe.Message)}");
    }

    private static object ToDto(ProbeResult probe) => new
    {
        reachable = probe.Reachable,
        statusCode = probe.StatusCode,
        latencyMs = probe.LatencyMs,
        message = probe.Message,
    };
}
