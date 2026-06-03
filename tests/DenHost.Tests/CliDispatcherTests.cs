using DenHost.Cli;
using DenHost.Cli.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenHost.Tests;

public class CliDispatcherTests
{
    [Fact]
    public async Task Dispatch_NoArgs_DefaultsToRun_AndReturnsUnknownWhenUnregistered()
    {
        // With no positional arg, the dispatcher defaults to the "run" command.
        // "run" is not registered in this test, so the dispatcher should report
        // an unknown command and exit 2.
        var dispatcher = new CliDispatcher(
            Array.Empty<ICliCommand>(),
            NullLogger<CliDispatcher>.Instance);
        var cliHost = new BufferingCliHost();
        var exit = await dispatcher.DispatchAsync(cliHost, Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Dispatch_UnknownCommand_Exits2()
    {
        var dispatcher = new CliDispatcher(
            Array.Empty<ICliCommand>(),
            NullLogger<CliDispatcher>.Instance);
        var cliHost = new BufferingCliHost();
        var exit = await dispatcher.DispatchAsync(cliHost, new[] { "frobnicate" }, CancellationToken.None);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Dispatch_HelpFlag_ShowsUsage()
    {
        // "help" is a built-in shortcut handled by the dispatcher itself.
        var dispatcher = new CliDispatcher(
            new ICliCommand[] { new EchoCommand("echo", new List<string>()) },
            NullLogger<CliDispatcher>.Instance);
        var cliHost = new BufferingCliHost();
        var exit = await dispatcher.DispatchAsync(cliHost, new[] { "--help" }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("Usage:", cliHost.StandardOut);
        Assert.Contains("echo", cliHost.StandardOut);
    }

    [Fact]
    public async Task Dispatch_VersionFlag_PrintsVersion()
    {
        // "version" is a built-in shortcut handled by the dispatcher itself.
        var dispatcher = new CliDispatcher(
            Array.Empty<ICliCommand>(),
            NullLogger<CliDispatcher>.Instance);
        var cliHost = new BufferingCliHost();
        var exit = await dispatcher.DispatchAsync(cliHost, new[] { "version" }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("den-host", cliHost.StandardOut);
    }

    [Fact]
    public async Task Dispatch_CustomCommand_StripsCommandNameFromArgs()
    {
        var captured = new List<string>();
        var echoCommand = new EchoCommand("echo", captured);
        var dispatcher = new CliDispatcher(
            new ICliCommand[] { echoCommand },
            NullLogger<CliDispatcher>.Instance);
        var cliHost = new BufferingCliHost();
        var exit = await dispatcher.DispatchAsync(cliHost, new[] { "echo", "alpha", "beta" }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(new[] { "alpha", "beta" }, captured);
    }

    private sealed class EchoCommand : ICliCommand
    {
        private readonly List<string> _sink;
        public EchoCommand(string name, List<string> sink)
        {
            Name = name;
            _sink = sink;
        }
        public string Name { get; }
        public string Summary => "echo args for testing";
        public Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
        {
            foreach (var arg in context.Args)
            {
                _sink.Add(arg);
            }
            return Task.FromResult(0);
        }
    }
}
