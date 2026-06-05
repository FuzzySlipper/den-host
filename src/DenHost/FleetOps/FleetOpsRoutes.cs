using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DenHost.FleetOps;

/// <summary>
/// Maps FleetOps and health HTTP routes onto an ASP.NET Core endpoint route builder.
/// Routes are under /api/host/fleet-ops and /api/host/health.
/// </summary>
public static class FleetOpsRoutes
{
    public static void MapFleetOpsRoutes(this IEndpointRouteBuilder app)
    {
        // ---- Health endpoint ----
        app.MapGet("/api/host/health", () =>
        {
            return Results.Ok(new
            {
                service = "den-host",
                status = "healthy",
                generatedAt = DateTimeOffset.UtcNow
            });
        });

        // ---- FleetOps overview ----
        app.MapGet("/api/host/fleet-ops", async (
            FleetOpsService fleetOps,
            CancellationToken cancellationToken) =>
        {
            var result = await fleetOps.GetOverviewAsync(cancellationToken);
            return Results.Ok(result);
        });

        // ---- FleetOps action run ----
        app.MapPost("/api/host/fleet-ops/actions/{actionId}/runs", async (
            string actionId,
            FleetOpsActionRunRequest request,
            FleetOpsService fleetOps,
            CancellationToken cancellationToken) =>
        {
            var result = await fleetOps.ExecuteActionAsync(actionId, request, cancellationToken);
            return result.Status == "failed"
                ? Results.BadRequest(result)
                : Results.Ok(result);
        });

        // ---- FleetOps run detail ----
        app.MapGet("/api/host/fleet-ops/runs/{runId}", (
            string runId,
            FleetOpsService fleetOps) =>
        {
            var run = fleetOps.GetRun(runId);
            if (run is null)
                return Results.NotFound(new FleetOpsRunResponse(null));
            return Results.Ok(new FleetOpsRunResponse(run));
        });
    }
}
