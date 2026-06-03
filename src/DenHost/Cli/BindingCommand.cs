using System.Text.Json;
using DenHost.Cli.Hosting;
using DenHost.Clients;
using DenHost.Host;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host binding</c> command. Performs a single
/// adapter-binding probe against Core and prints the result.
/// Exit code: 0 = registered, 4 = not fresh (stale, endpoint missing,
/// or unknown).
/// </summary>
public sealed class BindingCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IBindingHealthProvider _provider;
    private readonly ICliHost _cliHost;

    public BindingCommand(IBindingHealthProvider provider, ICliHost cliHost)
    {
        _provider = provider;
        _cliHost = cliHost;
    }

    public string Name => "binding";

    public string Summary => "Register/read back the adapter binding with Core (one-shot).";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        var asJson = false;
        for (var i = 0; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host binding [--json]");
                    return 0;
                default:
                    context.Host.WriteErrorLine($"binding: unknown argument '{context.Args[i]}'");
                    return 2;
            }
        }

        var health = await _provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (asJson)
        {
            context.Host.WriteLine(FormatJson(health));
        }
        else
        {
            context.Host.WriteLine(FormatText(health));
        }
        return health.IsFresh ? 0 : 4;
    }

    private static string FormatText(AdapterBindingHealth health)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Adapter binding");
        sb.AppendLine($"  state       = {health.State.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  last_seen   = {(health.LastSeen?.ToString("O") ?? "-")}");
        sb.AppendLine($"  last_error  = {(string.IsNullOrEmpty(health.LastError) ? "-" : health.LastError)}");
        if (health.BlockerEvidencePath is not null)
        {
            sb.AppendLine($"  blocker     = {health.BlockerEvidencePath}");
        }
        return sb.ToString();
    }

    private static string FormatJson(AdapterBindingHealth health) => JsonSerializer.Serialize(new
    {
        state = health.State.ToString().ToLowerInvariant(),
        lastSeen = health.LastSeen,
        lastError = health.LastError,
        blockerEvidencePath = health.BlockerEvidencePath,
        isFresh = health.IsFresh,
    }, s_jsonOptions);
}
