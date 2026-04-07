namespace ParserService.Core.Models;

public record CollectionResult(
    Guid TaskId,
    Guid JobId,
    string Source,
    Guid CompanyId,
    DateTimeOffset CollectedAt,
    IReadOnlyList<RawReview> Reviews
);
