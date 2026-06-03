using DenHost.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DenHost.Services;

/// <summary>
/// Minimal background service that emits a periodic log line so
/// <c>den-host run</c> is observably doing something. The actual
/// worker-pool machinery (binding heartbeat, Channels event reader,
/// run/process reconciliation) lands in tasks #1915/#1916/#1918.
/// </summary>
public sealed class HostHeartbeatService : BackgroundService
{
    private static readonly TimeSpan s_heartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly AdapterIdentity _identity;
    private readonly ILogger<HostHeartbeatService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public HostHeartbeatService(
        AdapterIdentity identity,
        ILogger<HostHeartbeatService> logger,
        IHostApplicationLifetime lifetime)
    {
        _identity = identity;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "den-host up: kind={Kind} instance_id={InstanceId} host={Host} roles=[{Roles}] caps=[{Caps}]",
            _identity.Kind,
            _identity.InstanceId,
            _identity.Host,
            string.Join(",", _identity.ManagedRoles),
            string.Join(",", _identity.ManagedCapabilities));

        try
        {
            using var timer = new PeriodicTimer(s_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _logger.LogDebug("den-host heartbeat: instance_id={InstanceId}", _identity.InstanceId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
