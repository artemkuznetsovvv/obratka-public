using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Integration.ProcessingGateway;

public interface IProcessingGatewayClient
{
    Task<AnalysisJobListResponse> ListAnalysesAsync(
        string? status, Guid? companyId, int? limit, int? offset, CancellationToken ct);

    Task<AnalysisJobDto?> GetAnalysisAsync(Guid jobId, CancellationToken ct);
}
