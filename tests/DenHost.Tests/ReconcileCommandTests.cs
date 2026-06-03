using DenHost.Cli;
using DenHost.Cli.Hosting;
using DenHost.Worker;

namespace DenHost.Tests;

public class ReconcileCommandTests
{
    [Fact]
    public async Task ExecuteAsync_AllReadopted_Exits0()
    {
        var service = new FakeReconciliationService(new[]
        {
            new ReconciliationReport("run-1", 1, ReconciliationOutcome.ReAdopted, "alive pid=1234", "active", null, null),
        });
        var cmd = new ReconcileCommand(service, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("run-1", output);
        Assert.Contains("readopted", output);
    }

    [Fact]
    public async Task ExecuteAsync_QuarantinePresent_Exits1()
    {
        var service = new FakeReconciliationService(new[]
        {
            new ReconciliationReport("run-1", 1, ReconciliationOutcome.ReAdopted, "alive pid=1234", "active", null, null),
            new ReconciliationReport("run-2", 2, ReconciliationOutcome.UncleanShutdownQuarantined, "gone", "active", "marker present", "/tmp/evidence.json"),
        });
        var cmd = new ReconcileCommand(service, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExecuteAsync_JsonOutput_ContainsKey()
    {
        var service = new FakeReconciliationService(new[]
        {
            new ReconciliationReport("run-1", 1, ReconciliationOutcome.ReAdopted, "alive", "active", null, null),
        });
        var cmd = new ReconcileCommand(service, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "--json" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"outcome\": \"readopted\"", ((BufferingCliHost)context.Host).StandardOut);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownArg_Exits2()
    {
        var service = new FakeReconciliationService(Array.Empty<ReconciliationReport>());
        var cmd = new ReconcileCommand(service, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), new[] { "--bogus" });

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task ExecuteAsync_NoReports_Exits0()
    {
        var service = new FakeReconciliationService(Array.Empty<ReconciliationReport>());
        var cmd = new ReconcileCommand(service, new BufferingCliHost());
        var context = new CliContext(new BufferingCliHost(), Array.Empty<string>());

        var exit = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exit);
        var output = ((BufferingCliHost)context.Host).StandardOut;
        Assert.Contains("no active runs", output);
    }

    private sealed class FakeReconciliationService : DenHost.Worker.IReconciliationService
    {
        private readonly IReadOnlyList<ReconciliationReport> _reports;
        public FakeReconciliationService(IReadOnlyList<ReconciliationReport> reports) { _reports = reports; }
        public Task<IReadOnlyList<ReconciliationReport>> ReconcileOnceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_reports);
    }
}
