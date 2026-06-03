using System.Text.Json;
using DenHost.Cli.Hosting;
using DenHost.Harness;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host smoke &lt;module-name&gt;</c> command. Looks up
/// the harness module by name, runs its <see cref="IHarnessModule.SmokeAsync"/>,
/// and prints the result. Exit 0 = passed, 1 = failed, 2 = blocked.
/// </summary>
public sealed class SmokeCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IEnumerable<IHarnessModule> _modules;
    private readonly ICliHost _cliHost;

    public SmokeCommand(IEnumerable<IHarnessModule> modules, ICliHost cliHost)
    {
        _modules = modules;
        _cliHost = cliHost;
    }

    public string Name => "smoke";

    public string Summary => "Run a smoke against one or all harness modules (subcommand: 'all' or module name).";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        var asJson = false;
        string? targetName = null;
        var all = false;
        for (var i = 0; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "all":
                    all = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp(context.Host);
                    return 0;
                default:
                    targetName = context.Args[i];
                    break;
            }
        }

        // Default: no target specified -> run every enabled module.
        if (!all && targetName is null)
        {
            all = true;
        }

        var targets = (all, targetName) switch
        {
            (true, _) => _modules.ToList(),
            (false, null) => _modules.ToList(),
            (false, { } n) => _modules.Where(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase)).ToList(),
        };

        if (targets.Count == 0)
        {
            context.Host.WriteErrorLine($"smoke: no harness module named '{targetName}' (configured: {string.Join(", ", _modules.Select(m => m.Name))})");
            return 2;
        }

        var results = new List<HarnessSmokeResult>(targets.Count);
        foreach (var module in targets)
        {
            HarnessSmokeResult r;
            try
            {
                r = await module.SmokeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                r = new HarnessSmokeResult(module.Name, module.Kind, HarnessSmokeOutcome.Failed,
                    $"smoke threw: {ex.GetType().Name}: {ex.Message}");
            }
            results.Add(r);
        }

        if (asJson)
        {
            context.Host.WriteLine(FormatJson(results));
        }
        else
        {
            context.Host.WriteLine(FormatText(results));
        }

        // Exit: 0 if all passed, 1 if any failed, 2 if all blocked (no actual failure but also no pass).
        if (results.All(r => r.Outcome == HarnessSmokeOutcome.Passed)) return 0;
        if (results.Any(r => r.Outcome == HarnessSmokeOutcome.Failed)) return 1;
        return 2;
    }

    private void PrintHelp(ICliHost host)
    {
        host.WriteLine("Usage: den-host smoke [all|<module-name>] [--json]");
        host.WriteLine("");
        host.WriteLine("Runs the harness module smoke test. With no argument, runs the smoke against");
        host.WriteLine("every enabled module. With a module name, runs only that module. With 'all',");
        host.WriteLine("explicitly runs every module. Exit 0 = passed, 1 = failed, 2 = blocked.");
    }

    private static string FormatText(IReadOnlyList<HarnessSmokeResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Harness smoke ({results.Count} module{(results.Count == 1 ? "" : "s")})");
        foreach (var r in results)
        {
            sb.AppendLine($"  - {r.ModuleName} ({r.Kind}): {r.Outcome.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(r.Detail)) sb.AppendLine($"      detail: {r.Detail}");
            if (r.BlockerEvidencePath is not null) sb.AppendLine($"      blocker: {r.BlockerEvidencePath}");
        }
        return sb.ToString();
    }

    private static string FormatJson(IReadOnlyList<HarnessSmokeResult> results) => JsonSerializer.Serialize(
        results.Select(r => new
        {
            moduleName = r.ModuleName,
            kind = r.Kind.ToString(),
            outcome = r.Outcome.ToString().ToLowerInvariant(),
            detail = r.Detail,
            blockerEvidencePath = r.BlockerEvidencePath,
        }),
        s_jsonOptions);
}
