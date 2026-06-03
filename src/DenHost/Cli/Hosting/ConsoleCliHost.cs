namespace DenHost.Cli.Hosting;

/// <summary>
/// Default <see cref="ICliHost"/> that writes to the process
/// standard streams. Text encoding follows the process default
/// (typically UTF-8 with no BOM on Linux/macOS).
/// </summary>
public sealed class ConsoleCliHost : ICliHost
{
    public void WriteLine(string message)
    {
        Console.Out.WriteLine(message);
    }

    public void WriteErrorLine(string message)
    {
        Console.Error.WriteLine(message);
    }
}
