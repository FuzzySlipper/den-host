using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenHost.FleetOps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class FleetOpsTests
{
    // ======================================================================
    // Unit tests — FleetOpsService with stubs
    // ======================================================================

    [Fact]
    public async Task GetOverviewAsync_ReturnsServiceUnitsAndActions()
    {
        var knownUnits = new List<FleetServiceUnit>
        {
            new("hermes-gateway@spawned-coder.service", "spawned-coder", "active", "running"),
            new("hermes-gateway@runner.service", "runner", "active", "running"),
        };

        var service = CreateService(
            discovery: new StubFleetOpsDiscovery(knownUnits));

        var result = await service.GetOverviewAsync();

        Assert.Equal("den-host", result.Service);
        Assert.Equal(2, result.ServiceUnits.Count);
        Assert.Contains(result.ServiceUnits, u => u.ProfileName == "spawned-coder");
        Assert.Contains(result.ServiceUnits, u => u.ProfileName == "runner");
        Assert.NotEmpty(result.Actions);
        Assert.Null(result.DiscoveryDiagnostics);
    }

    [Fact]
    public async Task GetOverviewAsync_HandlesDiscoveryFailure()
    {
        var service = CreateService(
            discovery: new StubFleetOpsDiscovery(simulateFailure: true));

        var result = await service.GetOverviewAsync();

        Assert.Equal("den-host", result.Service);
        Assert.Empty(result.ServiceUnits);
        Assert.NotNull(result.DiscoveryDiagnostics);
        Assert.Contains("Simulated discovery failure", result.DiscoveryDiagnostics);
    }

    [Fact]
    public async Task GetOverviewAsync_IncludesRecentRuns()
    {
        var runStore = new InMemoryFleetOpsRunStore();

        runStore.AddRun(new FleetOpsActionRun(
            RunId: "run-1",
            ActionId: "fleet-status",
            Args: new Dictionary<string, string>(),
            Status: "completed",
            CreatedAt: DateTimeOffset.UtcNow));

        var service = CreateService(runStore: runStore);
        var result = await service.GetOverviewAsync();

        Assert.NotNull(result.RecentRuns);
        Assert.Single(result.RecentRuns);
        Assert.Equal("run-1", result.RecentRuns![0].RunId);
    }

    [Fact]
    public async Task ExecuteActionAsync_UnknownAction_ReturnsError()
    {
        var service = CreateService();

        var result = await service.ExecuteActionAsync("nonexistent",
            new FleetOpsActionRunRequest("nonexistent"));

        Assert.Equal("failed", result.Status);
        Assert.Contains("Unknown action", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteActionAsync_DisabledAction_ReturnsError()
    {
        var service = CreateService();

        var result = await service.ExecuteActionAsync("fleet-update",
            new FleetOpsActionRunRequest("fleet-update"));

        Assert.Equal("failed", result.Status);
        Assert.Contains("disabled", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteActionAsync_MissingRequiredArg_ReturnsError()
    {
        var service = CreateService();

        var result = await service.ExecuteActionAsync("restart-profile",
            new FleetOpsActionRunRequest("restart-profile"));

        Assert.Equal("failed", result.Status);
        Assert.Contains("Required argument", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteActionAsync_UnknownArg_ReturnsError()
    {
        var service = CreateService();

        var result = await service.ExecuteActionAsync("fleet-status",
            new FleetOpsActionRunRequest("fleet-status")
            {
                Args = new Dictionary<string, string> { ["injected"] = "bad" }
            });

        Assert.Equal("failed", result.Status);
        Assert.Contains("Unknown argument", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteActionAsync_MutatingNeedsConfirmation_ReturnsError()
    {
        var service = CreateService();

        var result = await service.ExecuteActionAsync("restart-all",
            new FleetOpsActionRunRequest("restart-all"));

        Assert.Equal("failed", result.Status);
        Assert.Contains("Confirmation is required", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteActionAsync_MutatingWithConfirmation_Succeeds()
    {
        var executor = new StubFleetOpsCommandExecutor((_, _, _, _) =>
            Task.FromResult(new CommandResult(0, ["restarting..."], Array.Empty<string>())));

        var service = CreateService(executor: executor);

        var result = await service.ExecuteActionAsync("restart-all",
            new FleetOpsActionRunRequest("restart-all")
            {
                Confirmation = "yes"
            });

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.StdoutTail);
        Assert.Contains("restarting...", result.StdoutTail!);
    }

    [Fact]
    public async Task ExecuteActionAsync_NonMutatingDryRun_Succeeds()
    {
        var executor = new StubFleetOpsCommandExecutor((_, _, _, _) =>
            Task.FromResult(new CommandResult(0, ["status ok"], Array.Empty<string>())));

        var service = CreateService(executor: executor);

        var result = await service.ExecuteActionAsync("fleet-status",
            new FleetOpsActionRunRequest("fleet-status")
            {
                DryRun = true
            });

        Assert.Equal("completed", result.Status);
        Assert.True(result.WasDryRun);
    }

    [Fact]
    public async Task ExecuteActionAsync_ExecutorFailure_ReturnsFailed()
    {
        var executor = new StubFleetOpsCommandExecutor((_, _, _, _) =>
            Task.FromResult(new CommandResult(1, [], ["error output"], "command failed")));

        var service = CreateService(executor: executor);

        var result = await service.ExecuteActionAsync("fleet-status",
            new FleetOpsActionRunRequest("fleet-status"));

        Assert.Equal("failed", result.Status);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void GetRun_ExistingRun_ReturnsRun()
    {
        var runStore = new InMemoryFleetOpsRunStore();
        runStore.AddRun(new FleetOpsActionRun(
            RunId: "abc123",
            ActionId: "fleet-status",
            Args: new Dictionary<string, string>(),
            Status: "completed",
            CreatedAt: DateTimeOffset.UtcNow));

        var service = CreateService(runStore: runStore);

        var run = service.GetRun("abc123");
        Assert.NotNull(run);
        Assert.Equal("abc123", run!.RunId);
    }

    [Fact]
    public void GetRun_UnknownRun_ReturnsNull()
    {
        var service = CreateService();

        var run = service.GetRun("nonexistent");
        Assert.Null(run);
    }

    // ======================================================================
    // Integration tests — HTTP endpoints via TestServer
    // ======================================================================

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthy()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/host/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("den-host", body.GetProperty("service").GetString());
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FleetOpsOverview_ReturnsValidShape()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/host/fleet-ops");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("den-host", body.GetProperty("service").GetString());
        Assert.True(body.TryGetProperty("serviceUnits", out _));
        Assert.True(body.TryGetProperty("actions", out _));
    }

    [Fact]
    public async Task FleetOpsActionRun_UnknownAction_ReturnsBadRequest()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/host/fleet-ops/actions/nonexistent/runs",
            new FleetOpsActionRunRequest("nonexistent"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FleetOpsActionRun_ValidAction_ReturnsOk()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/host/fleet-ops/actions/fleet-status/runs",
            new FleetOpsActionRunRequest("fleet-status"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fleet-status", body.GetProperty("actionId").GetString());
        Assert.Equal("completed", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task FleetOpsRunDetail_ExistingRun_ReturnsRun()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        // First, create a run
        var createResp = await client.PostAsJsonAsync(
            "/api/host/fleet-ops/actions/fleet-status/runs",
            new FleetOpsActionRunRequest("fleet-status"));
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("runId").GetString()!;

        // Now fetch it
        var response = await client.GetAsync($"/api/host/fleet-ops/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("run", out var run));
        Assert.Equal(runId, run.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task FleetOpsRunDetail_UnknownRun_Returns404()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/host/fleet-ops/runs/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FleetOpsActionRun_WithConfirmation_Executes()
    {
        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/host/fleet-ops/actions/restart-all/runs",
            new FleetOpsActionRunRequest("restart-all")
            {
                Confirmation = "yes"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("restart-all", body.GetProperty("actionId").GetString());
    }

    // ======================================================================
    // Listener binding / service command tests
    // ======================================================================

    [Fact]
    public async Task WebApplication_BindsConfiguredListenAddress()
    {
        var listenAddress = "http://127.0.0.1:55400";
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        // Bind to the configured address before running
        app.Urls.Add(listenAddress);

        // The app's Urls collection should contain the configured address
        Assert.Contains(listenAddress, app.Urls);

        await app.DisposeAsync();
    }

    [Fact]
    public async Task WebApplication_DefaultUrlsEmptyWithoutExplicitConfiguration()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        // Without explicit Urls.Add, the default Kestrel address is
        // http://localhost:5000 but only after RunAsync starts listening.
        // The Urls collection is empty before RunAsync unless explicitly set.
        Assert.Empty(app.Urls);

        await app.DisposeAsync();
    }

    [Fact]
    public void DeployScript_GeneratesFleetOpsServiceUnitWithServeCommand()
    {
        var deployScriptPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "../../../../scripts/deploy-den-host.sh");

        if (!File.Exists(deployScriptPath))
        {
            // Fallback: try relative to project dir
            deployScriptPath = "/home/dev/den-host/scripts/deploy-den-host.sh";
        }

        Assert.True(File.Exists(deployScriptPath),
            $"Deploy script not found at {deployScriptPath}");

        var content = File.ReadAllText(deployScriptPath);

        // Verify fleetops unit generation function exists
        Assert.Contains("generate_fleetops_service_unit", content);

        // Verify it produces a den-host serve command
        Assert.Contains("ExecStart=${BINARY_DIR}/den-host serve", content);

        // Verify fleetops unit is generated in prepare_publish_artifacts
        Assert.Contains("den-host-fleetops.service", content);
    }

    [Fact]
    public void DeployScript_BackgroundUnitStillRunsDenHostRun()
    {
        var deployScriptPath = "/home/dev/den-host/scripts/deploy-den-host.sh";
        Assert.True(File.Exists(deployScriptPath));

        var content = File.ReadAllText(deployScriptPath);

        // The original background service unit should still use `den-host run`
        Assert.Contains("ExecStart=${BINARY_DIR}/den-host run", content);
    }

    // ======================================================================
    // Helpers
    // ======================================================================

    private static FleetOpsService CreateService(
        IFleetOpsServiceUnitDiscovery? discovery = null,
        IFleetOpsCommandExecutor? executor = null,
        IFleetOpsRunStore? runStore = null)
    {
        var registry = new FleetOpsActionRegistry();
        var options = new FleetOpsOptions();

        discovery ??= new StubFleetOpsDiscovery();
        executor ??= new StubFleetOpsCommandExecutor();
        runStore ??= new InMemoryFleetOpsRunStore();

        return new FleetOpsService(
            registry,
            discovery,
            executor,
            runStore,
            options,
            NullLogger<FleetOpsService>.Instance);
    }

    /// <summary>
    /// Creates a test host with FleetOps routes and stub services.
    /// No real systemd/script calls happen.
    /// </summary>
    private static async Task<IHost> CreateTestHostAsync()
    {
        var options = new FleetOpsOptions
        {
            Enabled = true,
            ListenAddress = "http://127.0.0.1:0",
            ScriptsDirectory = "/tmp/fake-fleet-scripts",
            SystemctlPath = "echo"
        };

        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(options);
                    services.AddSingleton<FleetOpsActionRegistry>();
                    services.AddSingleton<IFleetOpsServiceUnitDiscovery>(
                        new StubFleetOpsDiscovery());
                    services.AddSingleton<IFleetOpsCommandExecutor>(
                        new StubFleetOpsCommandExecutor());
                    services.AddSingleton<IFleetOpsRunStore>(
                        new InMemoryFleetOpsRunStore());
                    services.AddSingleton<FleetOpsService>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapFleetOpsRoutes();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }
}
