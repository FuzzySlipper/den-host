using DenHost.Configuration;
using DenHost.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DenHost.Services;

/// <summary>
/// Background service that periodically registers / heartbeats this
/// host's adapter binding with Core. Uses
/// <see cref="IBindingHealthProvider"/> so the probe logic is shared
/// with the <c>den-host binding</c> one-shot CLI command. The first
/// probe runs synchronously at host startup; subsequent probes run
/// on the configured interval.
/// </summary>
public sealed class AdapterBindingHeartbeatService : BackgroundService
{
    private readonly IBindingHealthProvider _provider;
    private readonly RuntimeOptions _runtime;
    private readonly ILogger<AdapterBindingHeartbeatService> _logger;

    public AdapterBindingHeartbeatService(
        IBindingHealthProvider provider,
        RuntimeOptions runtime,
        ILogger<AdapterBindingHeartbeatService> logger)
    {
        _provider = provider;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _runtime.BindingHeartbeatSeconds;
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation(
                "Adapter binding heartbeat disabled (Runtime:BindingHeartbeatSeconds={Seconds}).", intervalSeconds);
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        _logger.LogInformation(
            "Adapter binding heartbeat starting; interval={Seconds}s", intervalSeconds);

        // Initial probe at startup.
        await SafeProbeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeProbeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SafeProbeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _provider.ProbeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The provider is supposed to translate infrastructure failures
            // into structured health states, so an unhandled exception here
            // indicates a bug. Log and continue rather than crashing the host.
            _logger.LogError(ex, "Adapter binding probe threw unexpectedly; will retry on interval");
        }
    }
}
