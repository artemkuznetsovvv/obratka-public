using ProcessingGateway.Application.Ingestion;
using ProcessingGateway.Application.Llm;

namespace ProcessingGateway.Infrastructure.Storage;

/// Bucket `obratka-jobs` (ADR-001/004, schema 2.0). Префиксы:
///   {jobId}/raw/{source}.json        — пишет Parser, читаем мы
///   {jobId}/input.json               — пишем мы, читает LLM
///   {jobId}/output_reviews.json      — пишет LLM, читаем мы
///   {jobId}/output_summary.json      — пишет LLM, читаем мы
public interface IJobBlobStorage
{
    /// Скачивает и десериализует `raw/{source}.json` от Parser-а.
    Task<CollectionResultPayload> ReadRawAsync(Guid jobId, string sourceSlug, CancellationToken ct = default);

    /// Скачивает и десериализует `raw/{source}.json` по полному URL `s3://...`,
    /// который Parser положил в `CollectionTaskStatusResponse.S3Url`.
    Task<CollectionResultPayload> ReadRawAsync(string s3Url, CancellationToken ct = default);

    /// Сериализует и заливает `input.json` для LLM.
    Task WriteInputAsync(Guid jobId, LlmInput input, CancellationToken ct = default);

    /// Симметричное чтение `input.json` (нужно тестам).
    Task<LlmInput> ReadInputAsync(Guid jobId, CancellationToken ct = default);

    /// Скачивает `output_reviews.json` (per-review aspects). Читает по полному `s3://` URL.
    Task<LlmReviewsOutput> ReadReviewsOutputAsync(string s3Url, CancellationToken ct = default);

    /// Скачивает `output_summary.json` (job-level recommendations). Читает по полному `s3://` URL.
    Task<LlmSummaryOutput> ReadSummaryOutputAsync(string s3Url, CancellationToken ct = default);

    /// Симметричная запись `output_reviews.json` (для тестов и QA-инжектов).
    Task WriteReviewsOutputAsync(Guid jobId, LlmReviewsOutput output, CancellationToken ct = default);

    /// Симметричная запись `output_summary.json` (для тестов и QA-инжектов).
    Task WriteSummaryOutputAsync(Guid jobId, LlmSummaryOutput output, CancellationToken ct = default);
}

/// Парсинг `s3://bucket/key` в (bucket, key). Используется тестами и
/// `S3JobBlobStorage.ReadRawAsync(s3Url)`.
public static class S3UrlParser
{
    public static (string Bucket, string Key) Parse(string s3Url)
    {
        if (string.IsNullOrWhiteSpace(s3Url) || !s3Url.StartsWith("s3://", StringComparison.Ordinal))
            throw new ArgumentException($"Expected 's3://bucket/key', got '{s3Url}'", nameof(s3Url));

        var rest = s3Url["s3://".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            throw new ArgumentException($"Malformed s3 URL '{s3Url}'", nameof(s3Url));

        return (rest[..slash], rest[(slash + 1)..]);
    }
}
