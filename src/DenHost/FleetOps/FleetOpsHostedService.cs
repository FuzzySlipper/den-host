using DenHost.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.FleetOps;

/// <summary>
/// Background service that starts a bounded Kestrel HTTP server for the
/// FleetOps API. Runs alongside the existing Generic Host CLI/background
/// services without converting the whole app to Web SDK.
/// </summary>
public sealed class FleetOpsHostedService : IHostedService
{
    private readonly FleetOpsService _fleetOps;
    private readonly FleetOpsOptions _options;
    private readonly ILogger<FleetOpsHostedService> _logger;
    private WebApplication? _app;

    public FleetOpsHostedService(
        FleetOpsService fleetOps,
        IOptions<FleetOpsOptions> options,
        ILogger<FleetOpsHostedService> logger)
    {
        _fleetOps = fleetOps ?? throw new ArgumentNullException(nameof(fleetOps));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("FleetOps HTTP API is disabled");
            return;
        }

        var builder = WebApplication.CreateBuilder();

        // Minimal Kestrel-only host
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls(_options.ListenAddress);

        // No implicit appsettings loading — we only serve the FleetOps API
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        _app = builder.Build();

        // --- Health ---
        _app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "den-host" }));

        // --- FleetOps overview ---
        _app.MapGet("/api/host/fleet-ops", async (CancellationToken ct) =>
        {
            try
            {
                var overview = await _fleetOps.GetOverviewAsync(ct);
                return Results.Ok(overview);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FleetOps overview failed");
                return Results.Problem(detail: "FleetOps overview failed", statusCode: 500);
            }
        });

        // --- Execute action ---
        _app.MapPost("/api/host/fleet-ops/actions/{actionId}/runs",
            async (string actionId, FleetOpsActionRunRequest request, CancellationToken ct) =>
        {
            // Ensure actionId from route matches request body
            if (!string.IsNullOrWhiteSpace(request.ActionId) &&
                !string.Equals(request.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: $"Action ID mismatch: route '{actionId}' vs body '{request.ActionId}'",
                    statusCode: 400);
            }

            var bodyWithRouteActionId = request with { ActionId = actionId };
            var result = await _fleetOps.ExecuteActionAsync(actionId, bodyWithRouteActionId, ct);
            return result.Status == "failed"
                ? Results.Ok(result) // still 200 with error status — client-friendly
                : Results.Ok(result);
        });

        // --- Run lookup ---
        _app.MapGet("/api/host/fleet-ops/runs/{runId}",
            (string runId) =>
        {
            var run = _fleetOps.GetRun(runId);
            if (run is null)
                return Results.NotFound(new { error = $"Run '{runId}' not found" });
            return Results.Ok(new FleetOpsRunResponse(Run: run));
        });

        await _app.StartAsync(cancellationToken);
        _logger.LogInformation("FleetOps HTTP API started on {Address}", _options.ListenAddress);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            _logger.LogInformation("FleetOps HTTP API stopping");
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
