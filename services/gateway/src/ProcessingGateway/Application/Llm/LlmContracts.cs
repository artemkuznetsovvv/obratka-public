using System.Text.Json.Serialization;

namespace ProcessingGateway.Application.Llm;

/// `s3://obratka-jobs/{jobId}/input.json` — мы пишем, LLM читает (ADR-004 §2, schema 2.0).
/// `review_id` — long (отклонение от ADR-004, фиксируется в LLM_PYTHON_QUICKSTART.md).
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

// === Outputs (schema 2.0 — два файла) ============================================

/// `s3://obratka-jobs/{jobId}/output_reviews.json` — LLM пишет, мы читаем.
/// Per-review aspect-based анализ.
public record LlmReviewsOutput(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    [property: JsonPropertyName("reviews")] IReadOnlyList<LlmReviewResult> Reviews);

public record LlmReviewResult(
    [property: JsonPropertyName("review_id")] long ReviewId,
    [property: JsonPropertyName("text")] string Text,
    /// "позитивный" | "негативный" | "нейтральный" — закрытый русский enum.
    [property: JsonPropertyName("overall_sentiment")] string OverallSentiment,
    [property: JsonPropertyName("overall_confidence")] double OverallConfidence,
    [property: JsonPropertyName("aspects")] IReadOnlyList<LlmAspect> Aspects);

public record LlmAspect(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("sentiment")] string Sentiment,
    [property: JsonPropertyName("confidence")] double Confidence,
    /// Цитата из text. Может быть пустой строкой, если модель не нашла однозначной цитаты.
    [property: JsonPropertyName("fragment")] string Fragment,
    /// `true` если topic не из закрытого справочника (LLM-команда расширяет sometime).
    [property: JsonPropertyName("is_freeform")] bool IsFreeform);

/// `s3://obratka-jobs/{jobId}/output_summary.json` — LLM пишет, мы читаем.
/// Job-level рекомендации.
public record LlmSummaryOutput(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    /// Должен равняться `len(FullRecommendations)` (PG проверяет инвариант).
    [property: JsonPropertyName("recommendations_count")] int RecommendationsCount,
    /// Краткое резюме на 1–3 предложения. Для пустого input — fallback типа «Недостаточно данных...».
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("full_recommendations")] IReadOnlyList<LlmRecommendation> FullRecommendations);

public record LlmRecommendation(
    /// 1=критично, 2=важно, 3=полезно. Сортировка ASC при равенстве — больше evidence сверху.
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("expected_impact")] string? ExpectedImpact,
    /// Цитаты из отзывов или ссылки на review_id. Может быть `[]`, но не отсутствовать.
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence);
