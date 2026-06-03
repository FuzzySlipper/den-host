using System.Text.Json;
using DenHost.Cli.Hosting;
using DenHost.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace DenHost.Cli;

/// <summary>
/// One-shot <c>den-host reconcile</c> command. Runs a single
/// reconciliation pass and prints the per-run reports. Exit 0 = no
/// quarantines, 1 = at least one quarantined run.
/// </summary>
public sealed class ReconcileCommand : ICliCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReconciliationService _service;
    private readonly ICliHost _cliHost;

    public ReconcileCommand(IReconciliationService service, ICliHost cliHost)
    {
        _service = service;
        _cliHost = cliHost;
    }

    public string Name => "reconcile";

    public string Summary => "Run a single reconciliation pass and print per-run reports.";

    public async Task<int> ExecuteAsync(CliContext context, CancellationToken cancellationToken)
    {
        var asJson = false;
        for (var i = 0; i < context.Args.Count; i++)
        {
            switch (context.Args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "-h":
                case "--help":
                    context.Host.WriteLine("Usage: den-host reconcile [--json]");
                    return 0;
                default:
                    context.Host.WriteErrorLine($"reconcile: unknown argument '{context.Args[i]}'");
                    return 2;
            }
        }

        var reports = await _service.ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
        if (asJson)
        {
            context.Host.WriteLine(FormatJson(reports));
        }
        else
        {
            context.Host.WriteLine(FormatText(reports));
        }
        return reports.Any(r => IsQuarantine(r.Outcome)) ? 1 : 0;
    }

    private static bool IsQuarantine(ReconciliationOutcome outcome) =>
        outcome is ReconciliationOutcome.UncleanShutdownQuarantined
                  or ReconciliationOutcome.MismatchQuarantined
                  or ReconciliationOutcome.StaleAssignmentActive;

    private static string FormatText(IReadOnlyList<ReconciliationReport> reports)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Reconciliation pass ({reports.Count} run{(reports.Count == 1 ? "" : "s")} examined)");
        if (reports.Count == 0) sb.AppendLine("  (no active runs)");
        foreach (var r in reports)
        {
            var mark = IsQuarantine(r.Outcome) ? "QUAR" : "ok  ";
            sb.AppendLine($"  [{mark}] {r.LocalRunId} assignment={r.AssignmentId?.ToString() ?? "-"} outcome={r.Outcome.ToString().ToLowerInvariant()} process={r.ProcessObserved} assignment_state={r.AssignmentStateObserved}");
            if (!string.IsNullOrEmpty(r.Note)) sb.AppendLine($"        note: {r.Note}");
            if (r.EvidencePath is not null) sb.AppendLine($"        evidence: {r.EvidencePath}");
        }
        return sb.ToString();
    }

    private static string FormatJson(IReadOnlyList<ReconciliationReport> reports) => JsonSerializer.Serialize(
        reports.Select(r => new
        {
            localRunId = r.LocalRunId,
            assignmentId = r.AssignmentId,
            outcome = r.Outcome.ToString().ToLowerInvariant(),
            processObserved = r.ProcessObserved,
            assignmentStateObserved = r.AssignmentStateObserved,
            note = r.Note,
            evidencePath = r.EvidencePath,
        }),
        s_jsonOptions);
}
