using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class LocalBindingStateStoreTests : IDisposable
{
    private readonly string _tempDir;

    public LocalBindingStateStoreTests()
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

    private LocalBindingStateStore BuildStore()
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        return new LocalBindingStateStore(runtime, NullLogger<LocalBindingStateStore>.Instance);
    }

    [Fact]
    public async Task WriteBinding_RoundTripsThroughRead()
    {
        var store = BuildStore();
        var health = AdapterBindingHealth.Registered(DateTimeOffset.Parse("2026-06-03T10:00:00Z"));
        await store.WriteBindingAsync(health, CancellationToken.None);

        Assert.True(File.Exists(store.BindingFilePath));
        var roundTrip = await store.ReadBindingAsync(CancellationToken.None);
        Assert.NotNull(roundTrip);
        Assert.Equal(AdapterBindingState.Registered, roundTrip!.State);
        Assert.Equal(health.LastSeen, roundTrip.LastSeen);
    }

    [Fact]
    public async Task ReadBinding_ReturnsNullWhenFileMissing()
    {
        var store = BuildStore();
        var roundTrip = await store.ReadBindingAsync(CancellationToken.None);
        Assert.Null(roundTrip);
    }

    [Fact]
    public async Task WriteBlocker_OverwritesAndIsIdempotent()
    {
        var store = BuildStore();
        await store.WriteBlockerAsync("first reason", CancellationToken.None);
        await store.WriteBlockerAsync("second reason", CancellationToken.None);

        Assert.True(File.Exists(store.BlockerFilePath));
        var content = await File.ReadAllTextAsync(store.BlockerFilePath);
        Assert.Contains("second reason", content);
    }

    [Fact]
    public void DeleteBlockerIfPresent_RemovesFile()
    {
        var store = BuildStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.BlockerFilePath)!);
        File.WriteAllText(store.BlockerFilePath, "{}");
        store.DeleteBlockerIfPresent();
        Assert.False(File.Exists(store.BlockerFilePath));
    }

    [Fact]
    public void DeleteBlockerIfPresent_NoOpWhenAbsent()
    {
        var store = BuildStore();
        store.DeleteBlockerIfPresent();
        Assert.False(File.Exists(store.BlockerFilePath));
    }

    [Fact]
    public async Task WriteBinding_CreatesStateDirIfMissing()
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state", "nested", "deeper"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        var store = new LocalBindingStateStore(runtime, NullLogger<LocalBindingStateStore>.Instance);
        await store.WriteBindingAsync(AdapterBindingHealth.Registered(DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(Directory.Exists(runtime.StateDir));
    }
}
