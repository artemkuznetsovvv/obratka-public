namespace Obratka.Modules.Reports;

public interface IReportsModule
{
    Task<string> GenerateReportAsync(Guid analysisJobId, Guid companyId, CancellationToken ct);
}
