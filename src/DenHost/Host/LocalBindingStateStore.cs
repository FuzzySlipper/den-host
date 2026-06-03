using System.Text.Json;
using DenHost.Clients;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;

namespace DenHost.Host;

/// <summary>
/// Atomic local persistence for the adapter binding state and the
/// Core-binding-endpoint blocker evidence. Files live under
/// <c>RuntimeOptions.StateDir</c>. Writes are atomic (temp file +
/// rename) so a partial write cannot leave the host in a confused state
/// on restart.
/// </summary>
public sealed class LocalBindingStateStore
{
    public const string BindingFileName = "binding.json";
    public const string BlockerFileName = "blocker-binding-endpoint-missing.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RuntimeOptions _runtime;
    private readonly ILogger<LocalBindingStateStore> _logger;

    public LocalBindingStateStore(RuntimeOptions runtime, ILogger<LocalBindingStateStore> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public string BindingFilePath => Path.Combine(_runtime.StateDir, BindingFileName);
    public string BlockerFilePath => Path.Combine(_runtime.StateDir, BlockerFileName);

    /// <summary>
    /// Atomically write the current binding health to
    /// <c>&lt;StateDir&gt;/binding.json</c>.
    /// </summary>
    public async Task WriteBindingAsync(AdapterBindingHealth health, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runtime.StateDir);
        var dto = new BindingStateDto(
            StateName: health.State.ToString(),
            LastSeen: health.LastSeen,
            LastError: health.LastError,
            BlockerEvidencePath: health.BlockerEvidencePath,
            WrittenAt: DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(dto, s_jsonOptions);
        await WriteAtomicAsync(BindingFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the most recently written binding health, or null if the file
    /// does not exist or is unreadable.
    /// </summary>
    public async Task<AdapterBindingHealth?> ReadBindingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BindingFilePath))
        {
            return null;
        }
        try
        {
            await using var stream = File.OpenRead(BindingFilePath);
            var dto = await JsonSerializer.DeserializeAsync<BindingStateDto>(stream, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (dto is null)
            {
                return null;
            }
            if (!Enum.TryParse<AdapterBindingState>(dto.StateName, ignoreCase: true, out var state))
            {
                _logger.LogWarning("Binding state file had unknown state '{State}'; treating as Unknown", dto.StateName);
                state = AdapterBindingState.Unknown;
            }
            return new AdapterBindingHealth(state, dto.LastSeen, dto.LastError, dto.BlockerEvidencePath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read binding state file {Path}; treating as missing", BindingFilePath);
            return null;
        }
    }

    /// <summary>
    /// Write (or overwrite) the Core-binding-endpoint blocker evidence file.
    /// The file describes why the host cannot register a binding with Core,
    /// so an operator can see the blocker without trawling logs.
    /// </summary>
    public async Task WriteBlockerAsync(string reason, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_runtime.StateDir);
        var dto = new BlockerEvidenceDto(
            Reason: reason,
            DetectedAt: DateTimeOffset.UtcNow,
            StateDir: _runtime.StateDir);
        var json = JsonSerializer.Serialize(dto, s_jsonOptions);
        await WriteAtomicAsync(BlockerFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    public void DeleteBlockerIfPresent()
    {
        try
        {
            if (File.Exists(BlockerFilePath))
            {
                File.Delete(BlockerFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete blocker evidence file {Path}", BlockerFilePath);
        }
    }

    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(targetPath) ?? ".";
        var tempPath = Path.Combine(dir, "." + Path.GetFileName(targetPath) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            // File.Move with overwrite is atomic on POSIX when the source and
            // destination are on the same filesystem.
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

    private sealed record BindingStateDto(
        string StateName,
        DateTimeOffset? LastSeen,
        string? LastError,
        string? BlockerEvidencePath,
        DateTimeOffset WrittenAt);

    private sealed record BlockerEvidenceDto(
        string Reason,
        DateTimeOffset DetectedAt,
        string StateDir);
}
