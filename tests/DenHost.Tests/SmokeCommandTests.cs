using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Harness;

namespace DenHost.Tests;

public class SmokeCommandTests
{
    [Fact]
    public async Task ExecuteAsync_NoArg_RunsAllEnabledModules()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("a", new HarnessSmokeResult("a", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
            new FixedSmokeModule("b", new HarnessSmokeResult("b", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Blocked, "no binary", "/tmp/blocker")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        // One passed, one blocked: returns 2 (blocked is the only signal).
        Assert.Equal(2, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("a", output);
        Assert.Contains("b", output);
    }

    [Fact]
    public async Task ExecuteAsync_AllPassed_Exits0()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("a", new HarnessSmokeResult("a", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ExecuteAsync_AnyFailed_Exits1()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("a", new HarnessSmokeResult("a", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
            new FixedSmokeModule("b", new HarnessSmokeResult("b", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Failed, "broken")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExecuteAsync_TargetByName_RunsOnlyThatModule()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("alpha", new HarnessSmokeResult("alpha", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
            new FixedSmokeModule("beta", new HarnessSmokeResult("beta", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Failed, "broken")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "alpha" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("alpha", output);
        Assert.DoesNotContain("beta", output);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownName_Exits2()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("alpha", new HarnessSmokeResult("alpha", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "does-not-exist" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task ExecuteAsync_JsonOutput_ContainsKey()
    {
        var modules = new IHarnessModule[]
        {
            new FixedSmokeModule("h", new HarnessSmokeResult("h", HarnessModuleKind.Hermes, HarnessSmokeOutcome.Passed, "ok")),
        };
        var cmd = new SmokeCommand(modules, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "--json" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"outcome\": \"passed\"", ((BufferingCliHost)context.Host).StandardOut);
    }

    private sealed class FixedSmokeModule : IHarnessModule
    {
        private readonly HarnessSmokeResult _result;
        public FixedSmokeModule(string name, HarnessSmokeResult result) { Name = name; _result = result; }
        public string Name { get; }
        public HarnessModuleKind Kind => HarnessModuleKind.Hermes;
        public bool IsAvailable() => true;
        public Task<HarnessSmokeResult> SmokeAsync(CancellationToken cancellationToken) => Task.FromResult(_result);
    }
}
