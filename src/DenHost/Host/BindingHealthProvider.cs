using DenHost.Clients;
using DenHost.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DenHost.Host;

/// <summary>
/// Performs a single adapter-binding probe against Core. Used both by
/// the <c>den-host binding</c> one-shot CLI command and by the
/// AdapterBindingHeartbeatService background service.
/// On success, persists the fresh state to
/// <c>LocalBindingStateStore.BindingFilePath</c>. On
/// <see cref="NotSupportedException"/> (Core binding endpoint not
/// configured), writes blocker evidence rather than inventing a
/// Host-local truth table.
/// </summary>
public interface IBindingHealthProvider
{
    Task<AdapterBindingHealth> ProbeAsync(CancellationToken cancellationToken);
}

internal sealed class BindingHealthProvider : IBindingHealthProvider
{
    private readonly ICoreClient _core;
    private readonly AdapterIdentity _identity;
    private readonly LocalBindingStateStore _stateStore;
    private readonly ILogger<BindingHealthProvider> _logger;

    public BindingHealthProvider(
        ICoreClient core,
        AdapterIdentity identity,
        LocalBindingStateStore stateStore,
        ILogger<BindingHealthProvider> logger)
    {
        _core = core;
        _identity = identity;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<AdapterBindingHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        var request = new AdapterBindingRequest(
            AdapterKind: _identity.Kind,
            AdapterInstanceId: _identity.InstanceId,
            Host: _identity.Host,
            ManagedRoles: _identity.ManagedRoles,
            ManagedCapabilities: _identity.ManagedCapabilities,
            ProjectId: _identity.ProjectId);

        try
        {
            var snapshot = await _core.RegisterAdapterBindingAsync(request, cancellationToken).ConfigureAwait(false);
            var health = AdapterBindingHealth.Registered(snapshot.LastSeen);
            await _stateStore.WriteBindingAsync(health, cancellationToken).ConfigureAwait(false);
            _stateStore.DeleteBlockerIfPresent();
            _logger.LogInformation(
                "Adapter binding registered: instance_id={InstanceId} last_seen={LastSeen:O}",
                _identity.InstanceId, snapshot.LastSeen);
            return health;
        }
        catch (NotSupportedException ex)
        {
            // CoreOptions.BindingPath is not configured. Per den-host #1915 AC,
            // produce blocker evidence and report it rather than inventing a
            // Host-local truth table.
            await _stateStore.WriteBlockerAsync(ex.Message, cancellationToken).ConfigureAwait(false);
            var health = AdapterBindingHealth.EndpointMissing(ex.Message, _stateStore.BlockerFilePath);
            _logger.LogWarning(
                "Adapter binding blocked: Core binding endpoint not configured. " +
                "Blocker evidence written to {Path}. Configure Core:BindingPath or " +
                "create/link the Core contract task. ({Detail})",
                _stateStore.BlockerFilePath, ex.Message);
            return health;
        }
        catch (HttpRequestException ex)
        {
            // Core is unreachable. Fall back to last-known-good from the state file
            // so the operator can see when the binding was last fresh.
            var last = await _stateStore.ReadBindingAsync(cancellationToken).ConfigureAwait(false);
            var health = AdapterBindingHealth.Stale(last?.LastSeen, ex.Message);
            await _stateStore.WriteBindingAsync(health, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "Adapter binding stale: Core unreachable. last_seen={LastSeen}",
                last?.LastSeen);
            return health;
        }
    }
}
