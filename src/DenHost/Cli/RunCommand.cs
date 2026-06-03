using Microsoft.Extensions.Hosting;

namespace DenHost.Cli;

/// <summary>
/// Runs den-host as a long-lived Generic Host. Background services
/// (adapter binding heartbeat in #1915, Channels event reader in #1916,
/// worker run reconciliation in #1918) attach to the host here.
/// </summary>
public sealed class RunCommand : ICliCommand
{
    private readonly IHost _host;

    public RunCommand(IHost host)
    {
        _host = host;
    }

    public string Name => "run";

    public string Summary => "Run den-host as a service (foreground, Ctrl-C to stop).";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        context.Host.WriteLine("den-host: starting (Ctrl-C to stop).");
        await _host.RunAsync(cancellationToken).ConfigureAwait(false);
        context.Host.WriteLine("den-host: stopped.");
        return 0;
    }
}
