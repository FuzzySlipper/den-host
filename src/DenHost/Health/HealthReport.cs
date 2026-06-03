using DenHost.Clients;
using DenHost.Harness;
using DenHost.Host;

namespace DenHost.Health;

/// <summary>
/// A single harness module's reported state in the health report.
/// </summary>
public sealed record HarnessModuleHealth(
    string Name,
    HarnessModuleKind Kind,
    bool Enabled,
    bool Available);

/// <summary>
/// Aggregate health report for den-host. Composed of adapter
/// identity, Core/Channels reachability, adapter binding state, and
/// configured harness module availability. The host does not derive
/// a "running" / "stopped" status from local logs; only the live
/// probe results and the harness module's own IsAvailable() determine
/// status.
/// </summary>
public sealed record HealthReport(
    AdapterIdentity Identity,
    ProbeResult Core,
    ProbeResult Channels,
    AdapterBindingHealth Binding,
    IReadOnlyList<HarnessModuleHealth> HarnessModules,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// True if all configured components are healthy. "Healthy"
    /// means: Core reachable, Channels reachable, adapter binding
    /// fresh, and every enabled harness module reports itself
    /// available. Disabled modules are excluded from the check.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            if (!Core.Reachable) return false;
            if (!Channels.Reachable) return false;
            if (!Binding.IsFresh) return false;
            foreach (var module in HarnessModules)
            {
                if (module.Enabled && !module.Available) return false;
            }
            return true;
        }
    }
}
