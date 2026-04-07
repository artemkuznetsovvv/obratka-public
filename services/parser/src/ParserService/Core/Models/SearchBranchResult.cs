namespace ParserService.Core.Models;

public record SearchBranchResult(
    SourceType Source,
    string ExternalId,
    string ExternalUrl,
    string Name,
    string Address,
    double? Rating,
    int? ReviewCount
);
