using DenHost.Clients;
using DenHost.Harness;
using DenHost.Host;
using Microsoft.Extensions.Logging;

namespace DenHost.Health;

internal sealed class HealthReporter : IHealthReporter
{
    private readonly AdapterIdentity _identity;
    private readonly ICoreClient _core;
    private readonly IChannelsClient _channels;
    private readonly IEnumerable<IHarnessModule> _harnessModules;
    private readonly ILogger<HealthReporter> _logger;

    public HealthReporter(
        AdapterIdentity identity,
        ICoreClient core,
        IChannelsClient channels,
        IEnumerable<IHarnessModule> harnessModules,
        ILogger<HealthReporter> logger)
    {
        _identity = identity;
        _core = core;
        _channels = channels;
        _harnessModules = harnessModules;
        _logger = logger;
    }

    public async Task<HealthReport> BuildReportAsync(CancellationToken cancellationToken)
    {
        // Probe Core and Channels in parallel; they are independent.
        var coreTask = _core.GetHealthAsync(cancellationToken);
        var channelsTask = _channels.GetHealthAsync(cancellationToken);
        await Task.WhenAll(coreTask, channelsTask).ConfigureAwait(false);

        var coreResult = coreTask.Result;
        var channelsResult = channelsTask.Result;

        var modules = new List<HarnessModuleHealth>();
        foreach (var module in _harnessModules)
        {
            bool available;
            try
            {
                available = module.IsAvailable();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Harness module {Name} ({Kind}) IsAvailable threw", module.Name, module.Kind);
                available = false;
            }
            modules.Add(new HarnessModuleHealth(module.Name, module.Kind, Enabled: true, Available: available));
        }

        return new HealthReport(_identity, coreResult, channelsResult, modules, DateTimeOffset.UtcNow);
    }
}
