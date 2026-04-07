using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.Sources.YandexMaps;

public class YandexMapsPlugin : IReviewSourcePlugin
{
    public SourceType Source => SourceType.YandexMaps;

    public Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<SearchBranchResult>>([]);
    }

    public Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        throw new NotImplementedException("Yandex Maps plugin not yet implemented");
    }
}
