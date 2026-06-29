using ParserService.Core.Models;

namespace ParserService.Core;

public record CompanySearchRequest(string Query, string? City, SourceType[] Sources);

public interface IReviewSourcePlugin
{
    SourceType Source { get; }

    Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct);

    Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct);
}
