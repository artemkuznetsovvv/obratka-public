namespace ParserService.Core.Models;

public record RawReview(
    string ExternalId,
    string Text,
    DateTimeOffset Date,
    int Stars,
    Guid BranchId,
    string? AuthorName = null,
    string? AuthorPublicId = null,
    string? TextLanguage = null
);
