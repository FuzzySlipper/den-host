using DenHost.Clients;
using DenHost.Harness;
using DenHost.Health;
using DenHost.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class HealthReporterTests
{
    [Fact]
    public async Task BuildReportAsync_AggregatesCoreChannelsBindingAndHarness()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-1",
            Host = "host-1",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var core = new FakeCoreClient(ProbeResult.Ok(200, 3));
        var channels = new FakeChannelsClient(ProbeResult.Unreachable(null, 50, "Connection refused"));
        var binding = new FakeBindingProvider(AdapterBindingHealth.Registered(DateTimeOffset.UtcNow));
        var modules = new IHarnessModule[]
        {
            new StubHarnessModule("a"),
            new AvailableFakeModule("b"),
        };

        var reporter = new HealthReporter(
            identity, core, channels, binding, modules, NullLogger<HealthReporter>.Instance);

        var report = await reporter.BuildReportAsync(CancellationToken.None);

        Assert.Same(identity, report.Identity);
        Assert.True(report.Core.Reachable);
        Assert.False(report.Channels.Reachable);
        Assert.Equal(AdapterBindingState.Registered, report.Binding.State);
        Assert.Equal(2, report.HarnessModules.Count);
        Assert.Contains(report.HarnessModules, m => m.Name == "a" && !m.Available);
        Assert.Contains(report.HarnessModules, m => m.Name == "b" && m.Available);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task BuildReportAsync_AllGreen_ReportsHealthy()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-2",
            Host = "host-2",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var core = new FakeCoreClient(ProbeResult.Ok(200, 1));
        var channels = new FakeChannelsClient(ProbeResult.Ok(200, 1));
        var binding = new FakeBindingProvider(AdapterBindingHealth.Registered(DateTimeOffset.UtcNow));
        var modules = new IHarnessModule[] { new AvailableFakeModule("a") };

        var reporter = new HealthReporter(
            identity, core, channels, binding, modules, NullLogger<HealthReporter>.Instance);

        var report = await reporter.BuildReportAsync(CancellationToken.None);
        Assert.True(report.IsHealthy);
    }

    [Fact]
    public async Task BuildReportAsync_StaleBinding_Degrades()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-3",
            Host = "host-3",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var core = new FakeCoreClient(ProbeResult.Ok(200, 1));
        var channels = new FakeChannelsClient(ProbeResult.Ok(200, 1));
        var binding = new FakeBindingProvider(
            AdapterBindingHealth.Stale(DateTimeOffset.UtcNow.AddSeconds(-60), "Connection refused"));
        var modules = Array.Empty<IHarnessModule>();

        var reporter = new HealthReporter(
            identity, core, channels, binding, modules, NullLogger<HealthReporter>.Instance);

        var report = await reporter.BuildReportAsync(CancellationToken.None);
        Assert.False(report.IsHealthy);
        Assert.Equal(AdapterBindingState.Stale, report.Binding.State);
    }

    private sealed class FakeCoreClient : ICoreClient
    {
        private readonly ProbeResult _health;
        public FakeCoreClient(ProbeResult health) { _health = health; }
        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(_health);
        public Task<AdapterBindingSnapshot> RegisterAdapterBindingAsync(AdapterBindingRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");
        public Task<AdapterBindingSnapshot?> GetAdapterBindingAsync(string adapterInstanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");
    }

    private sealed class FakeChannelsClient : IChannelsClient
    {
        private readonly ProbeResult _health;
        public FakeChannelsClient(ProbeResult health) { _health = health; }
        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(_health);
        public Task<ChannelsEventPage> GetDirectAgentEventsAsync(
            long? channelId, string? projectId, long? afterId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: true));
        public Task<ChannelsEventReadback?> GetDirectAgentEventAsync(long eventId, CancellationToken cancellationToken) =>
            Task.FromResult<ChannelsEventReadback?>(null);
    }

    private sealed class FakeBindingProvider : DenHost.Host.IBindingHealthProvider
    {
        private readonly AdapterBindingHealth _health;
        public FakeBindingProvider(AdapterBindingHealth health) { _health = health; }
        public Task<AdapterBindingHealth> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(_health);
    }

    private sealed class AvailableFakeModule : IHarnessModule
    {
        public AvailableFakeModule(string name) { Name = name; }
        public string Name { get; }
        public HarnessModuleKind Kind => HarnessModuleKind.Stub;
        public bool IsAvailable() => true;
    }
}
