namespace ParserService.Core.Models;

public record RawReview(
    string ExternalId,
    string Text,
    DateTimeOffset Date,
    int Stars,
    Guid BranchId
);
