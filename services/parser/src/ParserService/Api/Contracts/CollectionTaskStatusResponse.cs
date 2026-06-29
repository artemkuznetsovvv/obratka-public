namespace ParserService.Api.Contracts;

public record CollectionTaskStatusResponse(
    Guid TaskId,
    string Status,
    string Source,
    double Progress,
    int? ReviewCount,
    string? S3Url,
    string? Error
);
