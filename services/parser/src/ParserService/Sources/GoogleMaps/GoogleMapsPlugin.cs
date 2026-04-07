using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.Sources.GoogleMaps;

public class GoogleMapsPlugin : IReviewSourcePlugin
{
    public SourceType Source => SourceType.GoogleMaps;

    public Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<SearchBranchResult>>([]);
    }

    public Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        throw new NotImplementedException("Google Maps plugin not yet implemented");
    }
}
