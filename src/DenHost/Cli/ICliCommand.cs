namespace DenHost.Cli;

/// <summary>
/// A single den-host CLI subcommand. Commands are registered
/// with the dispatcher and selected by the first positional arg.
/// </summary>
public interface ICliCommand
{
    /// <summary>
    /// The command name, e.g. "health", "run". Lowercase,
    /// no leading dashes. Must be unique across the registry.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// One-line summary used by <c>den-host help</c>.
    /// </summary>
    string Summary { get; }

    /// <summary>
    /// Execute the command. Return value is the process exit code.
    /// 0 = success, non-zero = error. The dispatcher is responsible
    /// for logging/exiting; commands should just return.
    /// </summary>
    Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken);
}
