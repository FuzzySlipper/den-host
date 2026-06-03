using System.Text.Json;
using DenHost.Cli.Hosting;
using DenHost.Configuration;
using Microsoft.Extensions.Options;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host quarantine list|show</c> command. Lists or
/// shows evidence files written by the reconciliation service under
/// <c>RuntimeOptions.QuarantineDir</c>.
/// </summary>
public sealed class QuarantineCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RuntimeOptions _runtime;
    private readonly ICliHost _cliHost;

    public QuarantineCommand(RuntimeOptions runtime, ICliHost cliHost)
    {
        _runtime = runtime;
        _cliHost = cliHost;
    }

    public string Name => "quarantine";

    public string Summary => "List or show quarantine evidence files (subcommand: 'list' or 'show').";

    public Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        if (context.Args.Count == 0)
        {
            PrintHelp(context.Host);
            return Task.FromResult(0);
        }

        return context.Args[0] switch
        {
            "list" => ListAsync(context),
            "show" => ShowAsync(context),
            "-h" or "--help" => HelpAsync(context),
            _ => UnknownAsync(context),
        };
    }

    private Task<int> ListAsync(CliContext context)
    {
        if (!Directory.Exists(_runtime.QuarantineDir))
        {
            context.Host.WriteLine($"(no quarantine dir at {_runtime.QuarantineDir})");
            return Task.FromResult(0);
        }
        var files = Directory.EnumerateFiles(_runtime.QuarantineDir, "reconciliation-*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            context.Host.WriteLine("(no quarantine evidence files)");
            return Task.FromResult(0);
        }
        foreach (var f in files) context.Host.WriteLine(f);
        return Task.FromResult(0);
    }

    private Task<int> ShowAsync(CliContext context)
    {
        if (context.Args.Count < 2)
        {
            context.Host.WriteErrorLine("quarantine show: missing file argument");
            return Task.FromResult(2);
        }
        var path = context.Args[1];
        if (!File.Exists(path))
        {
            context.Host.WriteErrorLine($"quarantine show: file not found: {path}");
            return Task.FromResult(2);
        }
        try
        {
            var raw = File.ReadAllText(path);
            // Pretty-print if it's JSON; otherwise pass through.
            using var doc = JsonDocument.Parse(raw);
            context.Host.WriteLine(JsonSerializer.Serialize(doc.RootElement, s_jsonOptions));
            return Task.FromResult(0);
        }
        catch (JsonException)
        {
            context.Host.WriteLine(File.ReadAllText(path));
            return Task.FromResult(0);
        }
    }

    private Task<int> HelpAsync(CliContext context)
    {
        PrintHelp(context.Host);
        return Task.FromResult(0);
    }

    private Task<int> UnknownAsync(CliContext context)
    {
        context.Host.WriteErrorLine($"quarantine: unknown subcommand '{context.Args[0]}'");
        PrintHelp(context.Host);
        return Task.FromResult(2);
    }

    private void PrintHelp(ICliHost host)
    {
        host.WriteLine("Usage: den-host quarantine <subcommand> [args]");
        host.WriteLine("");
        host.WriteLine("Subcommands:");
        host.WriteLine("  list                List reconciliation evidence files in the quarantine dir.");
        host.WriteLine("  show <file>         Print the contents of a single evidence file.");
    }
}
