namespace Obratka.Modules.Analytics;

internal sealed class AnalyticsModule : IAnalyticsModule
{
    public Task ComputeAggregatesAsync(Guid analysisJobId, CancellationToken ct)
        => throw new NotImplementedException();
}
