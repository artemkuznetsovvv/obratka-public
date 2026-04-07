namespace ParserService.Api.Contracts;

public record SearchResponse(IReadOnlyList<SearchBranchResultDto> Results);

public record SearchBranchResultDto(
    string Source,
    string ExternalId,
    string ExternalUrl,
    string Name,
    string Address,
    double? Rating,
    int? ReviewCount
);
