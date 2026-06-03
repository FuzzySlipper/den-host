using System.Text.Json;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;

namespace DenHost.Channels;

/// <summary>
/// Persists the Channels direct-agent event cursor for the shadow-mode
/// reader. Atomic temp-file + File.Move(overwrite) so a partial write
/// cannot leave the host in a confused state on restart.
/// </summary>
public sealed class EventCursorStore
{
    public const string CursorFileName = "channels-event-cursor.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RuntimeOptions _runtime;
    private readonly ILogger<EventCursorStore> _logger;

    public EventCursorStore(RuntimeOptions runtime, ILogger<EventCursorStore> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public string CursorFilePath => Path.Combine(_runtime.StateDir, CursorFileName);

    /// <summary>
    /// Read the most recently saved cursor, or null if no cursor is on disk.
    /// </summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CursorFilePath))
        {
            return null;
        }
        try
        {
            await using var stream = File.OpenRead(CursorFilePath);
            var dto = await JsonSerializer.DeserializeAsync<CursorDto>(stream, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return dto?.Cursor;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read channels event cursor at {Path}; treating as null", CursorFilePath);
            return null;
        }
    }

    /// <summary>
    /// Atomically write the cursor. A null cursor deletes the file.
    /// </summary>
    public async Task WriteAsync(string? cursor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runtime.StateDir);

        if (cursor is null)
        {
            try
            {
                if (File.Exists(CursorFilePath)) File.Delete(CursorFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete channels event cursor at {Path}", CursorFilePath);
            }
            return;
        }

        var dto = new CursorDto(cursor, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(dto, s_jsonOptions);
        await WriteAtomicAsync(CursorFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? ".";
        var tempPath = Path.Combine(dir, "." + Path.GetFileName(targetPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // best effort cleanup
            }
            throw;
        }
    }

    private sealed record CursorDto(string Cursor, DateTimeOffset WrittenAt);
}
