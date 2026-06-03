using DenHost.Health;

namespace DenHost.Cli;

public sealed class HealthCommand : ICliCommand
{
    private readonly IHealthReporter _reporter;

    public HealthCommand(IHealthReporter reporter)
    {
        _reporter = reporter;
    }

    public string Name => "health";

    public string Summary => "Probe Core/Channels and report local host/adapter identity.";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        var asJson = false;
        var exitNonZeroOnDegraded = true;

        for (var i = 0; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "--no-fail":
                    exitNonZeroOnDegraded = false;
                    break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host health [--json] [--no-fail]");
                    return 0;
                default:
                    context.Host.WriteLine($"health: unknown argument '{context.Args[i]}'");
                    return 2;
            }
        }

        var report = await _reporter.BuildReportAsync(cancellationToken).ConfigureAwait(false);
        var output = asJson
            ? HealthReportFormatter.FormatJson(report)
            : HealthReportFormatter.FormatText(report);
        context.Host.WriteLine(output);

        return (exitNonZeroOnDegraded && !report.IsHealthy) ? 3 : 0;
    }
}
