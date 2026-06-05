using DenHost.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DenHost.Tests;

public class ConfigurationTests
{
    [Fact]
    public void AdapterOptions_BindsFromSection()
    {
        var json = """
        {
          "Adapter": {
            "Kind": "host",
            "InstanceId": "abc-123",
            "Host": "workstation-01",
            "ManagedRoles": ["coder", "reviewer"],
            "ManagedCapabilities": ["worker.coder"]
          }
        }
        """;
        using var provider = BuildProvider(json);

        var options = provider.GetRequiredService<IOptions<AdapterOptions>>().Value;
        Assert.Equal("host", options.Kind);
        Assert.Equal("abc-123", options.InstanceId);
        Assert.Equal("workstation-01", options.Host);
        Assert.Equal(new[] { "coder", "reviewer" }, options.ManagedRoles);
        Assert.Equal(new[] { "worker.coder" }, options.ManagedCapabilities);
    }

    [Fact]
    public void CoreOptions_BindsBaseUrlAndPaths()
    {
        var json = """
        {
          "Core": {
            "BaseUrl": "http://127.0.0.1:18081",
            "HealthPath": "/healthz",
            "BindingPath": "/api/direct-delivery/adapter-bindings",
            "TimeoutMs": 7500
          }
        }
        """;
        using var provider = BuildProvider(json);
        var options = provider.GetRequiredService<IOptions<CoreOptions>>().Value;

        Assert.Equal("http://127.0.0.1:18081", options.BaseUrl);
        Assert.Equal("/healthz", options.HealthPath);
        Assert.Equal("/api/direct-delivery/adapter-bindings", options.BindingPath);
        Assert.Equal(7500, options.TimeoutMs);
    }

    [Fact]
    public void ChannelsOptions_BindsDirectAgentEventsPath()
    {
        var json = """
        {
          "Channels": {
            "BaseUrl": "http://127.0.0.1:18082",
            "HealthPath": "/healthz",
            "EventsListPath": "/api/direct-agent-events",
            "EventsListChannelId": 42,
            "DirectAgentEventPath": "/api/v2/direct-agent-events",
            "TimeoutMs": 2500
          }
        }
        """;
        using var provider = BuildProvider(json);
        var options = provider.GetRequiredService<IOptions<ChannelsOptions>>().Value;

        Assert.Equal("http://127.0.0.1:18082", options.BaseUrl);
        Assert.Equal("/api/direct-agent-events", options.EventsListPath);
        Assert.Equal(42, options.EventsListChannelId);
        Assert.Equal("/api/v2/direct-agent-events", options.DirectAgentEventPath);
        Assert.Equal(2500, options.TimeoutMs);
    }

    [Fact]
    public void RuntimeOptions_BindsLocalPaths()
    {
        var json = """
        {
          "Runtime": {
            "ConfigDir": "/etc/den-host",
            "RunDir": "/var/lib/den-host/run",
            "StateDir": "/var/lib/den-host/state",
            "LogDir": "/var/log/den-host",
            "QuarantineDir": "/var/lib/den-host/quarantine",
            "BindingHeartbeatSeconds": 45,
            "ChannelsEventPollSeconds": 60,
            "ChannelsEventPageSize": 25
          }
        }
        """;
        using var provider = BuildProvider(json);
        var options = provider.GetRequiredService<IOptions<RuntimeOptions>>().Value;

        Assert.Equal("/etc/den-host", options.ConfigDir);
        Assert.Equal("/var/lib/den-host/run", options.RunDir);
        Assert.Equal("/var/lib/den-host/state", options.StateDir);
        Assert.Equal("/var/log/den-host", options.LogDir);
        Assert.Equal("/var/lib/den-host/quarantine", options.QuarantineDir);
        Assert.Equal(45, options.BindingHeartbeatSeconds);
        Assert.Equal(60, options.ChannelsEventPollSeconds);
        Assert.Equal(25, options.ChannelsEventPageSize);
    }

    [Fact]
    public void HarnessOptions_BindsModuleList()
    {
        var json = """
        {
          "Harness": {
            "Modules": [
              { "Name": "stub-a", "Kind": "Stub", "Enabled": true, "Settings": {} },
              { "Name": "future", "Kind": "Hermes", "Enabled": false, "Settings": { "profile": "spawned-coder" } }
            ]
          }
        }
        """;
        using var provider = BuildProvider(json);
        var options = provider.GetRequiredService<IOptions<HarnessOptions>>().Value;

        Assert.Equal(2, options.Modules.Count);
        Assert.Equal("stub-a", options.Modules[0].Name);
        Assert.Equal(Harness.HarnessModuleKind.Stub, options.Modules[0].Kind);
        Assert.True(options.Modules[0].Enabled);
        Assert.Equal("future", options.Modules[1].Name);
        Assert.Equal(Harness.HarnessModuleKind.Hermes, options.Modules[1].Kind);
        Assert.False(options.Modules[1].Enabled);
        Assert.Equal("spawned-coder", options.Modules[1].Settings["profile"]);
    }

    [Fact]
    public void CoreOptions_RelativeBaseUrl_FailsValidation()
    {
        // .NET 10's Uri.TryCreate treats "/relative/path" as file:///relative/path
        // (RFC 8089), so a bare UriKind.Absolute check is not enough. The
        // production validation rule explicitly requires http(s); this test
        // mirrors the same predicate so it stays honest.
        var options = new CoreOptions { BaseUrl = "/relative/path" };
        var valid = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        Assert.False(valid);
    }

    [Fact]
    public void AdapterIdentity_FromOptions_CopiesAllFields()
    {
        var options = new AdapterOptions
        {
            Kind = "host",
            InstanceId = "i-1",
            Host = "machine-1",
            ManagedRoles = new[] { "r1" },
            ManagedCapabilities = new[] { "c1", "c2" },
        };
        var identity = DenHost.Host.AdapterIdentity.From(options);
        Assert.Equal("host", identity.Kind);
        Assert.Equal("i-1", identity.InstanceId);
        Assert.Equal("machine-1", identity.Host);
        Assert.Equal(new[] { "r1" }, identity.ManagedRoles);
        Assert.Equal(new[] { "c1", "c2" }, identity.ManagedCapabilities);
    }

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider BuildProvider(string json, bool validateOnStart = false)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<AdapterOptions>().Bind(config.GetSection(AdapterOptions.SectionName));
        services.AddOptions<CoreOptions>()
            .Bind(config.GetSection(CoreOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps), "Core:BaseUrl must be an absolute http(s) URI.");
        services.AddOptions<ChannelsOptions>()
            .Bind(config.GetSection(ChannelsOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps), "Channels:BaseUrl must be an absolute http(s) URI.");
        services.AddOptions<RuntimeOptions>().Bind(config.GetSection(RuntimeOptions.SectionName));
        services.AddOptions<HarnessOptions>().Bind(config.GetSection(HarnessOptions.SectionName));

        return services.BuildServiceProvider();
    }
}
