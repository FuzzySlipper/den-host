using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Harness;
using DenHost.Health;
using DenHost.Host;
using DenHost.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.Services;

/// <summary>
/// Wires den-host services into a <see cref="IServiceCollection"/>.
/// All Add* methods assume the host's <see cref="IConfiguration"/>
/// already has a den-host.json source attached.
/// </summary>
public static class DenHostServiceCollectionExtensions
{
    /// <summary>
    /// Register all den-host services (options, adapter identity,
    /// Core/Channels clients, health reporter, harness module slot,
    /// CLI surface, Generic Host background services).
    /// </summary>
    public static IServiceCollection AddDenHost(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Default log filter: den-host at Information, System.Net.Http.HttpClient
        // at Warning (the per-request Information logging is very noisy in
        // health/probe output). Operators can override with normal config:
        //   "Logging": { "LogLevel": { "System.Net.Http.HttpClient": "Information" } }
        services.AddLogging(builder =>
        {
            builder.AddFilter("DenHost", LogLevel.Information);
            builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
            builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        });
        // --- Options ---------------------------------------------------------
        services.AddOptions<AdapterOptions>()
            .Bind(configuration.GetSection(AdapterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<CoreOptions>()
            .Bind(configuration.GetSection(CoreOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => IsHttpUrl(o.BaseUrl),
                "Core:BaseUrl must be an absolute http(s) URI.")
            .ValidateOnStart();

        services.AddOptions<ChannelsOptions>()
            .Bind(configuration.GetSection(ChannelsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => IsHttpUrl(o.BaseUrl),
                "Channels:BaseUrl must be an absolute http(s) URI.")
            .ValidateOnStart();

        services.AddOptions<RuntimeOptions>()
            .Bind(configuration.GetSection(RuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<HarnessOptions>()
            .Bind(configuration.GetSection(HarnessOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // --- Adapter identity (singleton, derived from options) -------------
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AdapterOptions>>().Value;
            return AdapterIdentity.From(options);
        });

        // --- HTTP clients (typed, with BaseAddress from options) -----------
        services.AddHttpClient<ICoreClient, CoreClient>((sp, client) =>
        {
            var core = sp.GetRequiredService<IOptions<CoreOptions>>().Value;
            client.BaseAddress = new Uri(core.BaseUrl);
            client.Timeout = TimeSpan.FromMilliseconds(core.TimeoutMs);
        });

        services.AddHttpClient<IChannelsClient, ChannelsClient>((sp, client) =>
        {
            var channels = sp.GetRequiredService<IOptions<ChannelsOptions>>().Value;
            client.BaseAddress = new Uri(channels.BaseUrl);
            client.Timeout = TimeSpan.FromMilliseconds(channels.TimeoutMs);
        });

        // --- Health reporter ------------------------------------------------
        services.AddSingleton<IHealthReporter, HealthReporter>();

        // --- Harness module slot -------------------------------------------
        // For #1914 the only registered module is the Stub. Real harness
        // modules are wired in #1917. We resolve them from HarnessOptions
        // so the configuration slot is exercised end-to-end now.
        services.AddSingleton<IEnumerable<IHarnessModule>>(sp =>
        {
            var harness = sp.GetRequiredService<IOptions<HarnessOptions>>().Value;
            var list = new List<IHarnessModule>();
            foreach (var module in harness.Modules)
            {
                if (!module.Enabled)
                {
                    continue;
                }
                if (module.Kind == HarnessModuleKind.Stub)
                {
                    list.Add(new StubHarnessModule(module.Name));
                }
                else
                {
                    // Real harness modules live in their own assemblies and
                    // are loaded via MEF/MEF-like discovery in #1917. For
                    // #1914, only Stub is supported; anything else is ignored
                    // and logged so the operator can see the misconfiguration.
                    var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        .CreateLogger("DenHost.Harness");
                    logger.LogWarning(
                        "Harness module '{Name}' of kind {Kind} is configured but no real " +
                        "module implementation is registered yet; it will not be loaded. " +
                        "This is expected for #1914; real modules land in #1917.",
                        module.Name, module.Kind);
                }
            }
            return list;
        });

        // --- Background services (none in #1914; heartbeat in #1915) -------
        services.AddHostedService<HostHeartbeatService>();

        // --- CLI surface ----------------------------------------------------
        // Help and version are built into CliDispatcher to avoid a circular
        // dependency between the help command and the dispatcher.
        services.TryAddSingleton<ICliHost, ConsoleCliHost>();
        services.AddSingleton<HealthCommand>();
        services.AddSingleton<RunCommand>();
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<HealthCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<RunCommand>());
        services.AddSingleton<CliDispatcher>();

        return services;
    }

    private static bool IsHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}
