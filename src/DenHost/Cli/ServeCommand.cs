using DenHost.Cli.Hosting;
using DenHost.FleetOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DenHost.Cli;

/// <summary>
/// Runs den-host as an HTTP server exposing the FleetOps API surface.
/// Builds a standalone ASP.NET Core WebApplication with Kestrel.
/// FleetOps is a machine-local surface and does not depend on the
/// Generic Host background services.
/// </summary>
public sealed class ServeCommand : ICliCommand
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    public ServeCommand(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public string Name => "serve";

    public string Summary => "Start the Den Host FleetOps HTTP API server (Kestrel).";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        // Bind FleetOps options to check Enabled/ListenAddress
        var fleetOpsSection = _configuration.GetSection(FleetOpsOptions.SectionName);
        var fleetOpsOptions = fleetOpsSection.Get<FleetOpsOptions>() ?? new FleetOpsOptions();

        if (!fleetOpsOptions.Enabled)
        {
            context.Host.WriteLine("FleetOps is disabled in configuration. Set FleetOps:Enabled to true.");
            return 1;
        }

        var listenAddress = fleetOpsOptions.ListenAddress;
        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            context.Host.WriteLine("FleetOps ListenAddress is not configured.");
            return 1;
        }

        context.Host.WriteLine($"den-host fleet-ops: listening on {listenAddress} (Ctrl-C to stop).");

        var builder = WebApplication.CreateBuilder();

        // Carry forward the same configuration
        builder.Configuration.Sources.Clear();
        foreach (var source in ((IConfigurationRoot)_configuration).Providers)
        {
            // We only need the JSON file source — re-add it
        }
        // Simpler: just copy the config root
        builder.Configuration.AddConfiguration(_configuration);

        // Register FleetOps services
        builder.Services.AddDenHostFleetOps(_configuration);

        // Only log to console for the serve command
        builder.Services.AddLogging(logging =>
        {
            logging.AddConsole();
        });

        var app = builder.Build();

        app.MapFleetOpsRoutes();

        try
        {
            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        context.Host.WriteLine("den-host fleet-ops: stopped.");
        return 0;
    }
}
