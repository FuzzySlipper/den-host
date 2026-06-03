using System.Text;

namespace DenHost.Cli.Hosting;

/// <summary>
/// In-memory <see cref="ICliHost"/> used by tests to capture
/// command output without touching the real console streams.
/// </summary>
public sealed class BufferingCliHost : ICliHost
{
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();

    public void WriteLine(string message)
    {
        lock (_lock)
        {
            _stdout.AppendLine(message);
        }
    }

    public void WriteErrorLine(string message)
    {
        lock (_lock)
        {
            _stderr.AppendLine(message);
        }
    }

    public string StandardOut
    {
        get
        {
            lock (_lock)
            {
                return _stdout.ToString();
            }
        }
    }

    public string StandardError
    {
        get
        {
            lock (_lock)
            {
                return _stderr.ToString();
            }
        }
    }
}
