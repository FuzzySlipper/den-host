using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Tests;

public class BindingCommandTests
{
    [Fact]
    public async Task ExecuteAsync_TextOutput_ListsStateLastSeenAndError()
    {
        var binding = AdapterBindingHealth.Registered(DateTimeOffset.Parse("2026-06-03T10:00:00Z"));
        var cmd = new BindingCommand(new FixedBindingProvider(binding), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("state       = registered", output);
        Assert.Contains("2026-06-03T10:00:00", output);
    }

    [Fact]
    public async Task ExecuteAsync_StaleBinding_Exits4()
    {
        var binding = AdapterBindingHealth.Stale(DateTimeOffset.UtcNow.AddSeconds(-30), "Connection refused");
        var cmd = new BindingCommand(new FixedBindingProvider(binding), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(4, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("state       = stale", output);
    }

    [Fact]
    public async Task ExecuteAsync_EndpointMissing_Exits4AndPrintsBlocker()
    {
        var binding = AdapterBindingHealth.EndpointMissing(
            "Core:BindingPath not configured",
            "/tmp/state/blocker-binding-endpoint-missing.json");
        var cmd = new BindingCommand(new FixedBindingProvider(binding), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(4, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("state       = endpointmissing", output);
        Assert.Contains("blocker     = /tmp/state/blocker-binding-endpoint-missing.json", output);
    }

    [Fact]
    public async Task ExecuteAsync_JsonFlag_ProducesJson()
    {
        var binding = AdapterBindingHealth.Registered(DateTimeOffset.UtcNow);
        var cmd = new BindingCommand(new FixedBindingProvider(binding), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "--json" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("\"state\": \"registered\"", output);
        Assert.Contains("\"isFresh\": true", output);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownArg_Exits2()
    {
        var binding = AdapterBindingHealth.Registered(DateTimeOffset.UtcNow);
        var cmd = new BindingCommand(new FixedBindingProvider(binding), new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "--bogus" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    private sealed class FixedBindingProvider : IBindingHealthProvider
    {
        private readonly AdapterBindingHealth _health;
        public FixedBindingProvider(AdapterBindingHealth health) { _health = health; }
        public Task<AdapterBindingHealth> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_health);
    }
}
