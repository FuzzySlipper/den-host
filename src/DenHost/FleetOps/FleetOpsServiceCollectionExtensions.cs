using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.FleetOps;

/// <summary>
/// Extension methods for registering FleetOps services in the DI container.
/// </summary>
public static class FleetOpsServiceCollectionExtensions
{
    /// <summary>
    /// Register FleetOps services (options, action registry, discovery, executor, run store, service).
    /// Only registers real implementations when FleetOps.Enabled is true;
    /// otherwise registers no-op stubs.
    /// </summary>
    public static IServiceCollection AddDenHostFleetOps(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FleetOpsOptions>()
            .Bind(configuration.GetSection(FleetOpsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FleetOpsOptions>>().Value);

        // Action registry is always registered — it's pure data.
        services.AddSingleton<FleetOpsActionRegistry>();

        // Discovery, executor, and run store are registered conditionally.
        services.AddSingleton<IFleetOpsServiceUnitDiscovery>(sp =>
        {
            var options = sp.GetRequiredService<FleetOpsOptions>();
            if (!options.Enabled)
                return new StubFleetOpsDiscovery(diagnostics: "FleetOps is disabled");

            return new SystemdFleetOpsDiscovery(options, sp.GetRequiredService<ILogger<SystemdFleetOpsDiscovery>>());
        });

        services.AddSingleton<IFleetOpsCommandExecutor>(sp =>
        {
            var options = sp.GetRequiredService<FleetOpsOptions>();
            if (!options.Enabled)
                return new StubFleetOpsCommandExecutor();

            return new ProcessFleetOpsCommandExecutor(options, sp.GetRequiredService<ILogger<ProcessFleetOpsCommandExecutor>>());
        });

        services.AddSingleton<IFleetOpsRunStore>(sp =>
        {
            var options = sp.GetRequiredService<FleetOpsOptions>();
            return new InMemoryFleetOpsRunStore(options.MaxRuns);
        });

        services.AddSingleton<FleetOpsService>();

        return services;
    }
}
