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

public sealed record ParserSearchRequest(string Query, string? City, string[] Sources);

public sealed record ParserSearchBranchResult(
    string Source,
    string ExternalId,
    string ExternalUrl,
    string Name,
    string Address,
    double? Rating,
    int? ReviewCount);

public sealed record ParserSearchResponse(IReadOnlyList<ParserSearchBranchResult> Results);
