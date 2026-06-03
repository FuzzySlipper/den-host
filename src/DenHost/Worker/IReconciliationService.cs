namespace DenHost.Worker;

/// <summary>
/// Background-service-friendly interface for triggering a single
/// reconciliation pass. Used by the one-shot <c>den-host reconcile</c>
/// CLI command and by tests; the background service also implements
/// it (via <see cref="ReconciliationService"/>) so periodic and
/// on-demand passes share the same code path.
/// </summary>
public interface IReconciliationService
{
    Task<IReadOnlyList<ReconciliationReport>> ReconcileOnceAsync(CancellationToken cancellationToken);
}
