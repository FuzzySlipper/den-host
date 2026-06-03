using DenHost.Cli.Hosting;
using Microsoft.Extensions.Logging;

namespace DenHost.Cli;

/// <summary>
/// Maps a command-line invocation to a registered <see cref="ICliCommand"/>.
/// "help" and "version" are handled as built-ins so they work even when
/// no other commands are registered, and so the help command does not
/// need to depend on the dispatcher (which would be a circular dependency).
/// </summary>
public sealed class CliDispatcher
{
    private readonly IReadOnlyDictionary<string, ICliCommand> _commands;
    private readonly ILogger<CliDispatcher> _logger;

    public CliDispatcher(IEnumerable<ICliCommand> commands, ILogger<CliDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var map = new Dictionary<string, ICliCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                throw new ArgumentException("Command name must be non-empty.", nameof(commands));
            }
            if (!map.TryAdd(command.Name, command))
            {
                throw new ArgumentException(
                    $"Duplicate CLI command name '{command.Name}'.", nameof(commands));
            }
        }
        _commands = map;
        _logger = logger;
    }

    /// <summary>
    /// Dispatch a parsed command line to the matching command.
    /// Falls back to the built-in help if the name is unknown.
    /// </summary>
    /// <param name="host">CLI host providing services.</param>
    /// <param name="args">Full command-line args (argv).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> DispatchAsync(ICliHost host, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        // Default command when no positional arg is given: "run".
        var name = args.Count == 0 ? "run" : args[0];

        // Built-in help/version shortcuts handled directly.
        if (name is "--help" or "-h" or "help")
        {
            PrintHelp(host);
            return 0;
        }
        if (name is "--version" or "-v" or "version")
        {
            host.WriteLine("den-host " + VersionInfo.InformationalVersion
                + " (file_version=" + VersionInfo.FileVersion + ")");
            return 0;
        }

        return await ExecuteAsync(name, host, args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteAsync(string name, ICliHost host, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!_commands.TryGetValue(name, out var command))
        {
            _logger.LogError("Unknown command '{Name}'. Run 'den-host help' for usage.", name);
            return 2;
        }

        // Strip the command name from the args before passing to the command.
        var nameMatched = args.Count > 0 && args[0].Equals(name, StringComparison.OrdinalIgnoreCase);
        var remaining = nameMatched ? args.Skip(1).ToArray() : args.ToArray();

        var context = new CliContext(host, remaining);
        try
        {
            return await command.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130; // 128 + SIGINT
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command '{Name}' failed", name);
            return 1;
        }
    }

    private void PrintHelp(ICliHost host)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("den-host — harness-agnostic machine-local Den agent/runtime host");
        sb.AppendLine();
        sb.AppendLine("Usage: den-host [--config <path>] <command> [args]");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        foreach (var command in _commands)
        {
            sb.AppendLine($"  {command.Value.Name,-10}  {command.Value.Summary}");
        }
        sb.AppendLine("  help        Show this help.");
        sb.AppendLine("  version     Print den-host version information.");
        sb.AppendLine();
        sb.AppendLine("Config: den-host.json (or path from --config / DEN_HOST_CONFIG).");
        sb.AppendLine("        Endpoints and adapter identity are config-driven; no LAN topology in code.");
        host.WriteLine(sb.ToString());
    }
}
