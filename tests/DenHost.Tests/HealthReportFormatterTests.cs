using DenHost.Clients;
using DenHost.Harness;
using DenHost.Health;
using DenHost.Host;

namespace DenHost.Tests;

public class HealthReportFormatterTests
{
    [Fact]
    public void FormatText_IncludesAdapterIdentityAndProbeResults()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "den-host-01",
            Host = "workstation-01",
            ManagedRoles = new[] { "coder" },
            ManagedCapabilities = new[] { "worker.coder" },
        };
        var core = ProbeResult.Ok(200, 12);
        var channels = ProbeResult.Unreachable(null, 5000, "Connection refused (127.0.0.1:18082)");
        var modules = new[]
        {
            new HarnessModuleHealth("stub", HarnessModuleKind.Stub, Enabled: true, Available: false),
        };
        var report = new HealthReport(identity, core, channels, modules, DateTimeOffset.Parse("2026-06-03T10:00:00Z"));

        var text = HealthReportFormatter.FormatText(report);
        Assert.Contains("den-host-01", text);
        Assert.Contains("workstation-01", text);
        Assert.Contains("coder", text);
        Assert.Contains("worker.coder", text);
        Assert.Contains("reachable    = true", text);
        Assert.Contains("Connection refused", text);
        Assert.Contains("Overall: degraded", text);
    }

    [Fact]
    public void FormatText_ReportsHealthyWhenEverythingGreen()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "x",
            Host = "h",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var core = ProbeResult.Ok(200, 5);
        var channels = ProbeResult.Ok(200, 5);
        var modules = Array.Empty<HarnessModuleHealth>();
        var report = new HealthReport(identity, core, channels, modules, DateTimeOffset.UtcNow);

        var text = HealthReportFormatter.FormatText(report);
        Assert.Contains("Overall: healthy", text);
    }

    [Fact]
    public void FormatJson_IsValidJsonAndContainsKey()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i",
            Host = "h",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var report = new HealthReport(
            identity,
            ProbeResult.Ok(200, 1),
            ProbeResult.Ok(200, 1),
            Array.Empty<HarnessModuleHealth>(),
            DateTimeOffset.UtcNow);

        var json = HealthReportFormatter.FormatJson(report);
        Assert.Contains("\"isHealthy\": true", json);
        Assert.Contains("\"instanceId\": \"i\"", json);
    }
}
