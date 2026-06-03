namespace DenHost.Cli.Hosting;

/// <summary>
/// Output sink used by CLI commands. Implementations must be
/// thread-safe with respect to Write/WriteLine calls.
/// </summary>
public interface ICliHost
{
    /// <summary>
    /// Standard output (stdout).
    /// </summary>
    void WriteLine(string message);

    /// <summary>
    /// Standard error (stderr).
    /// </summary>
    void WriteErrorLine(string message);
}
