using DenHost.Channels;
using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.FleetOps;
using DenHost.Harness;
using DenHost.Harness.Modules.Hermes;
using DenHost.Health;
using DenHost.Host;
using DenHost.Services;
using DenHost.Worker;
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

        // Default log filter: den-host at Information, Microsoft.Hosting.Lifetime
        // at Information, and the entire System.Net.Http.HttpClient category tree
        // (including typed-client subcategories like
        // System.Net.Http.HttpClient.ICoreClient.ClientHandler) at Warning. The
        // typed-client Information logging is very noisy in health/probe output.
        // Operators can override with normal config:
        //   "Logging": { "LogLevel": { "System.Net.Http.HttpClient": "Information" } }
        services.AddLogging(builder =>
        {
            builder.AddFilter("DenHost", LogLevel.Information);
            builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
            builder.AddFilter("System.Net.Http.HttpClient*", LogLevel.Warning);
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

        // --- FleetOps options (no ValidateOnStart — optional section) -------
        services.AddOptions<FleetOpsOptions>()
            .Bind(configuration.GetSection(FleetOpsOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FleetOpsOptions>>().Value);

        // --- FleetOps services ------------------------------------------------
        services.AddSingleton<FleetOpsActionRegistry>();
        services.AddSingleton<IFleetOpsServiceUnitDiscovery, SystemdFleetOpsDiscovery>();
        services.AddSingleton<IFleetOpsCommandExecutor, ProcessFleetOpsCommandExecutor>();
        services.AddSingleton<IFleetOpsRunStore, InMemoryFleetOpsRunStore>();
        services.AddSingleton<FleetOpsService>();
        services.AddHostedService<FleetOpsHostedService>();

        // --- Adapter identity (singleton, derived from options) -------------
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AdapterOptions>>().Value;
            return AdapterIdentity.From(options);
        });

        // --- Runtime options (singleton, derived from IOptions) -----------
        // Registered as a concrete RuntimeOptions so hosted services can take
        // it directly. The Options system already runs ValidateOnStart, so
        // a single source of truth (IOptions<RuntimeOptions>.Value) is fine.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RuntimeOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CoreOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChannelsOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AdapterOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<HarnessOptions>>().Value);

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

        // --- Local binding state store + binding health provider ----------
        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<LocalBindingStateStore>();
            return new LocalBindingStateStore(runtime, logger);
        });
        services.AddSingleton<IBindingHealthProvider, BindingHealthProvider>();

        // --- Health reporter ------------------------------------------------
        services.AddSingleton<IHealthReporter, HealthReporter>();

        // --- Harness module slot -------------------------------------------
        // The first concrete harness module is Hermes (#1917). The factory
        // here is the only DenHost code that names HermesHarnessModule;
        // everything else uses IHarnessModule. Future modules will be
        // added as additional Kind branches.
        services.AddSingleton<IHermesProcessLauncher, SystemHermesProcessLauncher>();
        services.AddSingleton<IEnumerable<IHarnessModule>>(sp =>
        {
            var harness = sp.GetRequiredService<IOptions<HarnessOptions>>().Value;
            var runtime = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var list = new List<IHarnessModule>();
            foreach (var module in harness.Modules)
            {
                if (!module.Enabled) continue;
                switch (module.Kind)
                {
                    case HarnessModuleKind.Stub:
                        list.Add(new StubHarnessModule(module.Name));
                        break;
                    case HarnessModuleKind.Hermes:
                    {
                        var settings = HermesModuleSettings.From(module.Settings);
                        var launcher = sp.GetRequiredService<IHermesProcessLauncher>();
                        var moduleLogger = loggerFactory.CreateLogger<HermesHarnessModule>();
                        list.Add(new HermesHarnessModule(
                            module.Name, settings, runtime.LogDir, runtime.RunDir, launcher, moduleLogger));
                        break;
                    }
                    default:
                        loggerFactory.CreateLogger("DenHost.Harness").LogWarning(
                            "Harness module '{Name}' of kind {Kind} is configured but no implementation " +
                            "is registered yet; it will not be loaded. Future work.",
                            module.Name, module.Kind);
                        break;
                }
            }
            return list;
        });

        // --- Channels event reader (cursor + reader + shadow service) ------
        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<EventCursorStore>();
            return new EventCursorStore(runtime, logger);
        });
        services.AddSingleton<IChannelsEventReader, ChannelsEventReader>();

        // --- Local worker run registry + reconciliation -------------------
        services.AddSingleton<RunRegistry>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());

        // --- Background services ------------------------------------------
        services.AddHostedService<HostHeartbeatService>();
        services.AddHostedService<AdapterBindingHeartbeatService>();
        services.AddHostedService<ChannelsEventReaderService>();
        services.AddHostedService<ReconciliationService>();

        // --- CLI surface ----------------------------------------------------
        // Help and version are built into CliDispatcher to avoid a circular
        // dependency between the help command and the dispatcher.
        services.TryAddSingleton<ICliHost, ConsoleCliHost>();
        services.AddSingleton<HealthCommand>();
        services.AddSingleton<RunCommand>();
        services.AddSingleton<BindingCommand>();
        services.AddSingleton<EventsCommand>();
        services.AddSingleton<SmokeCommand>();
        services.AddSingleton<ReconcileCommand>();
        services.AddSingleton<QuarantineCommand>();
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<HealthCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<RunCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<BindingCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<EventsCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<SmokeCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<ReconcileCommand>());
        services.AddSingleton<ICliCommand>(sp => sp.GetRequiredService<QuarantineCommand>());
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
