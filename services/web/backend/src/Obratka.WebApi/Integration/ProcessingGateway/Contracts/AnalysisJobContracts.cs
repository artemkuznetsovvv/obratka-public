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
