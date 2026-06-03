using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.Clients;

/// <summary>
/// HTTP client for the Core endpoint. Uses an HttpClient provided
/// by <see cref="IHttpClientFactory"/> with a preconfigured BaseAddress.
/// All public methods are side-effect-free for the host (no local
/// state mutation); they only talk to Core over HTTP.
/// </summary>
public sealed class CoreClient : ICoreClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly CoreOptions _options;
    private readonly ILogger<CoreClient> _logger;

    public CoreClient(HttpClient http, IOptions<CoreOptions> options, ILogger<CoreClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProbeResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        var path = _options.HealthPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return ProbeResult.Unreachable(null, 0, "Core:HealthPath not configured");
        }

        return await ProbeAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdapterBindingSnapshot> RegisterAdapterBindingAsync(
        AdapterBindingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.BindingPath))
        {
            throw new NotSupportedException(
                "Core:BindingPath is not configured; cannot register adapter binding. " +
                "den-host task #1915 documents the blocking path: produce blocker evidence " +
                "and link to the Core contract task rather than inventing a Host-local truth table.");
        }

        // Shape assumed: PUT {BindingPath}/{adapterInstanceId} with the request body.
        // Core contract task #1901 is the source of truth for this URL; if it differs,
        // change this method only.
        var path = $"{_options.BindingPath.TrimEnd('/')}/{Uri.EscapeDataString(request.AdapterInstanceId)}";
        using var message = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(request, options: s_jsonOptions),
        };

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Core binding register failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var snapshot = await response.Content
            .ReadFromJsonAsync<AdapterBindingSnapshot>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            throw new InvalidOperationException("Core returned an empty adapter binding snapshot.");
        }

        return snapshot;
    }

    public async Task<AdapterBindingSnapshot?> GetAdapterBindingAsync(
        string adapterInstanceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(adapterInstanceId))
        {
            throw new ArgumentException("Adapter instance id is required.", nameof(adapterInstanceId));
        }

        if (string.IsNullOrWhiteSpace(_options.BindingPath))
        {
            return null;
        }

        var path = $"{_options.BindingPath.TrimEnd('/')}/{Uri.EscapeDataString(adapterInstanceId)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Core binding readback failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        return await response.Content
            .ReadFromJsonAsync<AdapterBindingSnapshot>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(method, path);
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();
            if (response.IsSuccessStatusCode)
            {
                return ProbeResult.Ok((int)response.StatusCode, sw.ElapsedMilliseconds);
            }

            return ProbeResult.Unreachable(
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative cancel; rethrow.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Core probe timed out at {Path}", path);
            return ProbeResult.Unreachable(null, sw.ElapsedMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Core probe failed at {Path}", path);
            return ProbeResult.Unreachable(null, sw.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Unexpected error probing Core at {Path}", path);
            return ProbeResult.Unreachable(null, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}
