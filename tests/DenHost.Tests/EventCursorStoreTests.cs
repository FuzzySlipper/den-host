using DenHost.Channels;
using DenHost.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class EventCursorStoreTests : IDisposable
{
    private readonly string _tempDir;

    public EventCursorStoreTests()
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

    private EventCursorStore BuildStore()
    {
        var runtime = new RuntimeOptions
        {
            ConfigDir = _tempDir,
            RunDir = Path.Combine(_tempDir, "run"),
            StateDir = Path.Combine(_tempDir, "state"),
            LogDir = Path.Combine(_tempDir, "log"),
            QuarantineDir = Path.Combine(_tempDir, "quarantine"),
        };
        return new EventCursorStore(runtime, NullLogger<EventCursorStore>.Instance);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullWhenFileMissing()
    {
        var store = BuildStore();
        Assert.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughRead()
    {
        var store = BuildStore();
        await store.WriteAsync(12345, CancellationToken.None);
        Assert.Equal(12345, await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_WithNullAfterId_DeletesFile()
    {
        var store = BuildStore();
        await store.WriteAsync(42, CancellationToken.None);
        Assert.True(File.Exists(store.CursorFilePath));
        await store.WriteAsync(null, CancellationToken.None);
        Assert.False(File.Exists(store.CursorFilePath));
    }
}
