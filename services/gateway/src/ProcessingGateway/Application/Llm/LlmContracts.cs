using System.Text.Json.Serialization;

namespace ProcessingGateway.Application.Llm;

/// `s3://obratka-jobs/{jobId}/input.json` — мы пишем, LLM читает (ADR-004 §2).
/// JSON snake_case кроме `processedReview` в LlmOutput, который явно camelCase в ADR.
/// `review_id` — long (отклонение от ADR-004 — Этап 0 №8). В FAQ на Этапе 6 фиксируется явно.
public record LlmInput(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    [property: JsonPropertyName("company_id")] Guid CompanyId,
    [property: JsonPropertyName("reviews")] IReadOnlyList<LlmInputReview> Reviews);

public record LlmInputReview(
    [property: JsonPropertyName("review_id")] long ReviewId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("source")] string Source,           // slug
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("stars")] int? Stars,
    [property: JsonPropertyName("branch_id")] Guid BranchId,
    [property: JsonPropertyName("text_language")] string? TextLanguage = null);

/// `s3://obratka-jobs/{jobId}/output.json` — LLM пишет, мы читаем (ADR-004 §2).
/// `processedReview` — camelCase сохраняется как в ADR.
/// `recommendation` — на уровне job (не per review).
public record LlmOutput(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    [property: JsonPropertyName("recommendation")] string? Recommendation,
    [property: JsonPropertyName("processedReview")] IReadOnlyList<LlmProcessedReview> ProcessedReview);

public record LlmProcessedReview(
    [property: JsonPropertyName("review_id")] long ReviewId,
    [property: JsonPropertyName("fake_status")] string FakeStatus,                     // "normal" | "suspicious" | "fake"
    [property: JsonPropertyName("fake_reason_tags")] IReadOnlyList<string> FakeReasonTags,
    [property: JsonPropertyName("sentiment")] string? Sentiment,
    [property: JsonPropertyName("sentiment_confidence")] double? SentimentConfidence,
    [property: JsonPropertyName("is_spam")] bool IsSpam,
    [property: JsonPropertyName("spam_confidence")] double SpamConfidence,
    [property: JsonPropertyName("topics")] IReadOnlyList<string> Topics);
