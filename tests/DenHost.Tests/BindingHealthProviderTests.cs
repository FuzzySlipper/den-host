using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class BindingHealthProviderTests : IDisposable
{
    private readonly string _tempDir;

    public BindingHealthProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "den-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private (BindingHealthProvider Provider, FakeCoreClient Core, LocalBindingStateStore Store) Build(
        AdapterIdentity identity, ICoreClient core)
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        var store = new LocalBindingStateStore(runtime, NullLogger<LocalBindingStateStore>.Instance);
        var provider = new BindingHealthProvider(
            core,
            identity,
            store,
            NullLogger<BindingHealthProvider>.Instance);
        return (provider, (FakeCoreClient)core, store);
    }

    [Fact]
    public async Task ProbeAsync_OnSuccess_ReturnsRegisteredAndWritesState()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-1",
            Host = "h-1",
            ManagedRoles = new[] { "coder" },
            ManagedCapabilities = new[] { "worker.coder" },
        };
        var fake = new FakeCoreClient(ProbeResult.Ok(200, 1));
        var (provider, _, store) = Build(identity, fake);

        var health = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(AdapterBindingState.Registered, health.State);
        Assert.True(health.IsFresh);
        Assert.True(File.Exists(store.BindingFilePath));
        var written = await store.ReadBindingAsync(CancellationToken.None);
        Assert.NotNull(written);
        Assert.Equal(AdapterBindingState.Registered, written!.State);
    }

    [Fact]
    public async Task ProbeAsync_OnEndpointMissing_WritesBlockerEvidence()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-2",
            Host = "h-2",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        var fake = new FakeCoreClient(ProbeResult.Ok(200, 1), bindEndpointMissing: true);
        var (provider, _, store) = Build(identity, fake);

        var health = await provider.ProbeAsync(CancellationToken.None);

        Assert.Equal(AdapterBindingState.EndpointMissing, health.State);
        Assert.False(health.IsFresh);
        Assert.NotNull(health.LastError);
        Assert.Equal(store.BlockerFilePath, health.BlockerEvidencePath);
        Assert.True(File.Exists(store.BlockerFilePath));
    }

    [Fact]
    public async Task ProbeAsync_OnCoreUnreachable_MarksStaleAndFallsBackToLastSeen()
    {
        var identity = new AdapterIdentity
        {
            Kind = "host",
            InstanceId = "i-3",
            Host = "h-3",
            ManagedRoles = Array.Empty<string>(),
            ManagedCapabilities = Array.Empty<string>(),
        };
        // First probe succeeds.
        var fake = new FakeCoreClient(ProbeResult.Ok(200, 1));
        var (provider, _, store) = Build(identity, fake);
        var first = await provider.ProbeAsync(CancellationToken.None);
        Assert.Equal(AdapterBindingState.Registered, first.State);

        // Now switch to unreachable and probe again.
        fake.NextProbeThrows(new HttpRequestException("Connection refused"));
        var second = await provider.ProbeAsync(CancellationToken.None);
        Assert.Equal(AdapterBindingState.Stale, second.State);
        Assert.False(second.IsFresh);
        Assert.NotNull(second.LastError);
        Assert.Equal(first.LastSeen, second.LastSeen);
    }

    private sealed class FakeCoreClient : ICoreClient
    {
        private readonly ProbeResult _health;
        private readonly bool _bindEndpointMissing;
        private Func<Task<AdapterBindingSnapshot>>? _registerOverride;

        public FakeCoreClient(ProbeResult health, bool bindEndpointMissing = false)
        {
            _health = health;
            _bindEndpointMissing = bindEndpointMissing;
        }

        public void NextProbeThrows(Exception ex)
        {
            _registerOverride = () => throw ex;
        }

        public Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_health);

        public Task<AdapterBindingSnapshot> RegisterAdapterBindingAsync(
            AdapterBindingRequest request, CancellationToken cancellationToken)
        {
            if (_registerOverride is not null)
            {
                return _registerOverride();
            }
            if (_bindEndpointMissing)
            {
                throw new NotSupportedException("Core:BindingPath is not configured; cannot register adapter binding.");
            }
            return Task.FromResult(new AdapterBindingSnapshot(
                AdapterInstanceId: request.AdapterInstanceId,
                AdapterKind: request.AdapterKind,
                Host: request.Host,
                ManagedRoles: request.ManagedRoles,
                ManagedCapabilities: request.ManagedCapabilities,
                LastSeen: DateTimeOffset.UtcNow,
                State: "active"));
        }

        public Task<AdapterBindingSnapshot?> GetAdapterBindingAsync(
            string adapterInstanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used in this test");
    }
}
