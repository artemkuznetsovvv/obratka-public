namespace ParserService.Api.Contracts;

public record CollectionTaskListItem(
    Guid TaskId,
    Guid JobId,
    Guid CompanyId,
    string Source,
    string Status,
    double Progress,
    int? ReviewCount,
    string? S3Url,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CollectionTaskListResponse(
    int Count,
    int Limit,
    int Offset,
    IReadOnlyList<CollectionTaskListItem> Items
);
