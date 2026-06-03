namespace DenHost.Health;

/// <summary>
/// Composes a <see cref="HealthReport"/> from the configured
/// adapter identity, Core/Channels clients, and harness modules.
/// </summary>
public interface IHealthReporter
{
    Task<HealthReport> BuildReportAsync(CancellationToken cancellationToken);
}
