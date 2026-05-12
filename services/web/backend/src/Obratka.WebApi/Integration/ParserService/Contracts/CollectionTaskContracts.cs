namespace Obratka.WebApi.Integration.ParserService.Contracts;

public sealed record ParserCollectionTaskItem(
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
    DateTimeOffset UpdatedAt);

public sealed record ParserCollectionTaskListResponse(
    int Count,
    int Limit,
    int Offset,
    IReadOnlyList<ParserCollectionTaskItem> Items);

public sealed record ParserCollectionTaskStatusResponse(
    Guid TaskId,
    string Status,
    string Source,
    double Progress,
    int? ReviewCount,
    string? S3Url,
    string? Error);

public sealed record ParserBranchTarget(Guid BranchId, string ExternalId, string ExternalUrl);

public sealed record CreateParserCollectionTaskRequest(
    Guid JobId,
    Guid CompanyId,
    string Source,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    List<ParserBranchTarget> Branches);

public sealed record CreateParserCollectionTaskResponse(Guid TaskId);
