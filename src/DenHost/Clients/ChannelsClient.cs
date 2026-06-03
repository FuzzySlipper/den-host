using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.Clients;

/// <summary>
/// HTTP client for the Channels endpoint. Mirrors <see cref="CoreClient"/>
/// for the probe path and adds direct-agent event reads (used by the
/// shadow-mode reader in den-host task #1916).
/// </summary>
public sealed class ChannelsClient : IChannelsClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ChannelsOptions _options;
    private readonly ILogger<ChannelsClient> _logger;

    public ChannelsClient(HttpClient http, IOptions<ChannelsOptions> options, ILogger<ChannelsClient> logger)
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
            return ProbeResult.Unreachable(null, 0, "Channels:HealthPath not configured");
        }

        return await ProbeAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DirectAgentEventPage> GetDirectAgentEventsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
        }

        var path = _options.DirectAgentEventsPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            // Channels event endpoint is genuinely required for #1916; for #1914 we
            // surface this as an empty page rather than throwing, so the health
            // command still works.
            return new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null);
        }

        var query = $"limit={Uri.EscapeDataString(limit.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        if (!string.IsNullOrEmpty(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        var fullPath = path.Contains('?') ? $"{path}&{query}" : $"{path}?{query}";

        using var message = new HttpRequestMessage(HttpMethod.Get, fullPath);
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Endpoint not yet implemented on Channels. Return an empty page so
            // shadow-mode consumers can run without crashing. #1916 will surface
            // this as a blocker evidence file if the endpoint is missing.
            return new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Channels event read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var page = await response.Content
            .ReadFromJsonAsync<DirectAgentEventPage>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return page ?? new DirectAgentEventPage(Array.Empty<DirectAgentEvent>(), null);
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
            throw;
        }
        catch (TaskCanceledException ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Channels probe timed out at {Path}", path);
            return ProbeResult.Unreachable(null, sw.ElapsedMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Channels probe failed at {Path}", path);
            return ProbeResult.Unreachable(null, sw.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Unexpected error probing Channels at {Path}", path);
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
