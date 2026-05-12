namespace Obratka.Modules.Reports;

internal sealed class ReportsModule : IReportsModule
{
    public Task<string> GenerateReportAsync(Guid analysisJobId, Guid companyId, CancellationToken ct)
        => throw new NotImplementedException();
}
