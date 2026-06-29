using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Integration.ProcessingGateway;

public interface IProcessingGatewayClient
{
    Task<AnalysisJobListResponse> ListAnalysesAsync(
        string? status, Guid? companyId, int? limit, int? offset, CancellationToken ct);

    Task<AnalysisJobDto?> GetAnalysisAsync(Guid jobId, CancellationToken ct);

    Task<StartAnalysisQaResponse> StartAnalysisAsync(StartAnalysisQaRequest request, CancellationToken ct);

    Task<RestartSourceQaResponse> RestartSourceAsync(
        Guid jobId, string source, RestartSourceQaRequest request, CancellationToken ct);

    Task LlmReplayAsync(Guid jobId, CancellationToken ct);

    Task<JobBlobList> ListJobBlobsAsync(Guid jobId, CancellationToken ct);

    Task<JobBlobContent?> DownloadJobBlobAsync(Guid jobId, string name, CancellationToken ct);

    Task<IReadOnlyList<BranchStatsItem>> GetBranchStatsAsync(Guid jobId, CancellationToken ct);
}
