namespace Obratka.Modules.Analytics;

public interface IAnalyticsModule
{
    Task ComputeAggregatesAsync(Guid analysisJobId, CancellationToken ct);
}
