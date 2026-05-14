using System.Text.Json.Serialization;

namespace Obratka.WebApi.Integration.ProcessingGateway.Contracts;

// ----- Raw (snake_case + camelCase mix as PG returns) — internal to client -----

internal sealed record RawAnalysisJob(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("company_id")] Guid CompanyId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("review_count")] int ReviewCount,
    [property: JsonPropertyName("collection_progress")] Dictionary<string, RawCollectionEntry> CollectionProgress,
    [property: JsonPropertyName("payload_url")] string? PayloadUrl,
    [property: JsonPropertyName("result_reviews_url")] string? ResultReviewsUrl,
    [property: JsonPropertyName("result_summary_url")] string? ResultSummaryUrl,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("recommendations_count")] int RecommendationsCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record RawCollectionEntry(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("reviewCount")] int? ReviewCount,
    [property: JsonPropertyName("s3Url")] string? S3Url,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record RawAnalysisJobListResponse(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("items")] IReadOnlyList<RawAnalysisJob> Items);

// ----- QA actions: requests in camelCase (PG defaults), responses snake_case from QA controllers -----

public sealed record StartAnalysisBranchSpec(
    Guid BranchId,
    string Source,
    string ExternalId,
    string ExternalUrl);

public sealed record StartAnalysisQaRequest(
    Guid CompanyId,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    IReadOnlyList<StartAnalysisBranchSpec> Branches,
    Guid? AnalysisJobId = null);

internal sealed record RawStartAnalysisResponse(
    [property: JsonPropertyName("analysisJobId")] Guid AnalysisJobId);

public sealed record StartAnalysisQaResponse(Guid AnalysisJobId);

public sealed record RestartSourceBranchSpec(
    Guid BranchId,
    string ExternalId,
    string ExternalUrl);

public sealed record RestartSourceQaRequest(
    IReadOnlyList<RestartSourceBranchSpec> Branches,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo);

internal sealed record RawRestartSourceResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("task_id")] Guid TaskId,
    [property: JsonPropertyName("previous_status")] string PreviousStatus,
    [property: JsonPropertyName("current_status")] string CurrentStatus);

public sealed record RestartSourceQaResponse(
    string Source,
    Guid TaskId,
    string PreviousStatus,
    string CurrentStatus);

internal sealed record RawJobBlobItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("last_modified")] DateTimeOffset LastModified);

internal sealed record RawJobBlobList(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] IReadOnlyList<RawJobBlobItem> Items);

public sealed record JobBlobItem(string Key, long Size, DateTimeOffset LastModified);

public sealed record JobBlobList(string Bucket, string Prefix, int Count, IReadOnlyList<JobBlobItem> Items);

public sealed record JobBlobContent(
    Stream Stream,
    string ContentType,
    string? FileName,
    long? ContentLength);

// ----- Public DTOs (clean camelCase for Web API response to frontend) -----

public sealed record AnalysisJobDto(
    Guid Id,
    Guid CompanyId,
    string Status,
    int ReviewCount,
    IReadOnlyDictionary<string, CollectionProgressDto> CollectionProgress,
    string? PayloadUrl,
    string? ResultReviewsUrl,
    string? ResultSummaryUrl,
    string? Summary,
    int RecommendationsCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record CollectionProgressDto(
    string TaskId,
    string Status,
    int Progress,
    int? ReviewCount,
    string? S3Url,
    string? Error);

public sealed record AnalysisJobListResponse(
    int Total,
    int Limit,
    int Offset,
    IReadOnlyList<AnalysisJobDto> Items);

internal static class AnalysisJobMapping
{
    public static AnalysisJobDto ToDto(RawAnalysisJob raw) => new(
        raw.Id,
        raw.CompanyId,
        raw.Status,
        raw.ReviewCount,
        raw.CollectionProgress.ToDictionary(
            kv => kv.Key,
            kv => new CollectionProgressDto(
                kv.Value.TaskId,
                kv.Value.Status,
                kv.Value.Progress,
                kv.Value.ReviewCount,
                kv.Value.S3Url,
                kv.Value.Error)),
        raw.PayloadUrl,
        raw.ResultReviewsUrl,
        raw.ResultSummaryUrl,
        raw.Summary,
        raw.RecommendationsCount,
        raw.CreatedAt,
        raw.SentAt,
        raw.CompletedAt,
        raw.Error);
}
