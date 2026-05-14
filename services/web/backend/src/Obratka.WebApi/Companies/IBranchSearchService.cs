using Obratka.WebApi.Contracts.Companies;

namespace Obratka.WebApi.Companies;

public interface IBranchSearchService
{
    Task<BranchSearchResponse> SearchAsync(
        Guid companyId, string query, string city, IReadOnlyList<string> sources, CancellationToken ct);
}
