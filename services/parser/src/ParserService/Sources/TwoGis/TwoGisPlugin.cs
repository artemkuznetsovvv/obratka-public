using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.Sources.TwoGis;

public class TwoGisPlugin : IReviewSourcePlugin
{
    public SourceType Source => SourceType.TwoGis;

    public Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<SearchBranchResult>>([]);
    }

    public Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        throw new NotImplementedException("2GIS plugin not yet implemented");
    }
}
