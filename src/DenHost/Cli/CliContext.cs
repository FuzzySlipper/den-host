namespace DenHost.Cli;

/// <summary>
/// Context passed to a CLI command. Includes the loaded
/// <see cref="Hosting.ICliHost"/> (which exposes services and
/// configuration) and the remaining command-line args after
/// the command name has been consumed.
/// </summary>
public sealed class CliContext
{
    public CliContext(Hosting.ICliHost host, IReadOnlyList<string> args)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Args = args ?? throw new ArgumentNullException(nameof(args));
    }

    public Hosting.ICliHost Host { get; }
    public IReadOnlyList<string> Args { get; }
}
