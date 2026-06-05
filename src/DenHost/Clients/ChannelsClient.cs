using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.Clients;

/// <summary>
/// HTTP client for the Channels endpoint. Mirrors
/// <see cref="CoreClient"/> in structure. All public methods are
/// side-effect-free for the host (no local state mutation); they
/// only talk to Channels over HTTP. Failure paths return structured
/// results rather than throwing, so the den-host shadow reader and
/// background services can decide locally what to do.
/// </summary>
public sealed class ChannelsClient : IChannelsClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    public async Task<ChannelsEventPage> GetDirectAgentEventsAsync(
        long? channelId,
        string? projectId,
        long? afterId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be positive.");
        }
        if (channelId is null && string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Provide either channelId or projectId.");
        }

        var path = _options.EventsListPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            // No list path configured; the reader treats this as "endpoint
            // not implemented" without crashing.
            return new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: false);
        }

        var query = new List<string>
        {
            $"limit={Uri.EscapeDataString(limit.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
        };
        if (channelId is long cid)
        {
            query.Add($"channelId={Uri.EscapeDataString(cid.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        if (!string.IsNullOrEmpty(projectId))
        {
            query.Add($"projectId={Uri.EscapeDataString(projectId)}");
        }
        if (afterId is long aid)
        {
            query.Add($"afterId={Uri.EscapeDataString(aid.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        var fullPath = path.Contains('?') ? $"{path}&{string.Join("&", query)}" : $"{path}?{string.Join("&", query)}";

        using var message = new HttpRequestMessage(HttpMethod.Get, fullPath);
        AddAuthIfPresent(message);

        try
        {
            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: false);
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Channels list read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            var envelope = await response.Content
                .ReadFromJsonAsync<ChannelsEventListEnvelope>(s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (envelope is null)
            {
                return new ChannelsEventPage(Array.Empty<ChannelsEvent>(), null, HasMore: false, EndpointImplemented: true);
            }
            return new ChannelsEventPage(
                Items: envelope.Items ?? Array.Empty<ChannelsEvent>(),
                NextAfterId: envelope.NextAfterId,
                HasMore: envelope.HasMore,
                EndpointImplemented: true);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogDebug(ex, "Channels list read timed out");
            throw new HttpRequestException("Channels list read timed out", ex);
        }
        catch (HttpRequestException)
        {
            throw;
        }
    }

    public async Task<ChannelsEventReadback?> GetDirectAgentEventAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventId), eventId, "Event id must be positive.");
        }
        var path = _options.DirectAgentEventPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var fullPath = $"{path.TrimEnd('/')}/{Uri.EscapeDataString(eventId.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

        using var message = new HttpRequestMessage(HttpMethod.Get, fullPath);
        AddAuthIfPresent(message);

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
                $"Channels event readback failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        return await response.Content
            .ReadFromJsonAsync<ChannelsEventReadback>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentWorkLifecycleWriteResult> PostAgentWorkLifecycleEventAsync(
        AgentWorkLifecycleWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_options.AgentWorkLifecyclePath))
        {
            return new AgentWorkLifecycleWriteResult(false, null, EndpointImplemented: false, null,
                "Channels:AgentWorkLifecyclePath not configured");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.AgentWorkLifecyclePath)
        {
            Content = JsonContent.Create(request, options: s_jsonOptions),
        };
        AddAuthIfPresent(message);

        try
        {
            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AgentWorkLifecycleWriteResult(false, (int)response.StatusCode, EndpointImplemented: false, null, body);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new AgentWorkLifecycleWriteResult(false, (int)response.StatusCode, EndpointImplemented: true, null, body);
            }
            var eventId = TryExtractId(body);
            return new AgentWorkLifecycleWriteResult(true, (int)response.StatusCode, EndpointImplemented: true, eventId, null);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogDebug(ex, "Channels lifecycle write timed out");
            return new AgentWorkLifecycleWriteResult(false, null, EndpointImplemented: true, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Channels lifecycle write failed");
            return new AgentWorkLifecycleWriteResult(false, null, EndpointImplemented: true, null, ex.Message);
        }
    }

    private static string? TryExtractId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("id", out var id)) return id.ToString();
        }
        catch
        {
            // Best-effort evidence enrichment only.
        }
        return null;
    }

    private void AddAuthIfPresent(HttpRequestMessage message)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    private async Task<ProbeResult> ProbeAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(method, path);
            AddAuthIfPresent(message);

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

    /// <summary>
    /// Wire shape of the den-channels list response
    /// (<c>ChannelsEventListEnvelope</c>). The reader does not need
    /// <c>HasMore</c> for paging (it uses <c>NextAfterId</c> directly)
    /// but Channels always returns it; we surface it on
    /// <see cref="ChannelsEventPage"/> for diagnostics.
    /// </summary>
    private sealed class ChannelsEventListEnvelope
    {
        public IReadOnlyList<ChannelsEvent>? Items { get; set; }
        public long? NextAfterId { get; set; }
        public bool HasMore { get; set; }
    }
}
