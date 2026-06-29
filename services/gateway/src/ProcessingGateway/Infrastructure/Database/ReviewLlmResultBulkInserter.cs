using System.Text.Json;
using Dapper;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Domain;

namespace ProcessingGateway.Infrastructure.Database;

/// Bulk-UPSERT LLM-результатов в `review_llm_results` (schema 2.0).
/// UNIQUE `(review_id, analysis_job_id)` + `ON CONFLICT DO UPDATE`: повторный прогон LLM по
/// тому же job-у (цикл live-мониторинга — модель «растущий seed-job») ОСВЕЖАЕТ разметку всех
/// отзывов (sentiment/confidence/aspects/processed_at), а не пропускает уже размеченные.
/// Остаётся идемпотентным: повторное применение того же output даёт тот же результат.
/// Сырьё `reviews` при этом не трогаем — там append-only (`ON CONFLICT (composite_key) DO NOTHING`).
///
/// `aspects` — JSONB; пишется как сериализованный list&lt;ReviewAspect&gt; в snake_case
/// (тот же JsonOptions, что в `ProcessingDbContext.JsonStringConverter`, чтобы EF при чтении
/// видел те же ключи).
public sealed class ReviewLlmResultBulkInserter
{
    /// Должен совпадать с `ProcessingDbContext.JsonOptions` (snake_case + WhenWritingNull).
    private static readonly JsonSerializerOptions AspectsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbConnectionFactory _factory;

    public ReviewLlmResultBulkInserter(IDbConnectionFactory factory) => _factory = factory;

    private const string Sql = @"
        INSERT INTO review_llm_results
            (review_id, analysis_job_id, overall_sentiment, overall_confidence, aspects, processed_at)
        SELECT
            review_id, analysis_job_id, overall_sentiment, overall_confidence, aspects::jsonb, NOW()
        FROM UNNEST(
            @ReviewIds, @AnalysisJobIds, @OverallSentiments, @OverallConfidences, @AspectsJson
        ) AS t(
            review_id, analysis_job_id, overall_sentiment, overall_confidence, aspects
        )
        ON CONFLICT (review_id, analysis_job_id) DO UPDATE SET
            overall_sentiment  = EXCLUDED.overall_sentiment,
            overall_confidence = EXCLUDED.overall_confidence,
            aspects            = EXCLUDED.aspects,
            processed_at       = EXCLUDED.processed_at;";

    public async Task<int> InsertAsync(
        Guid analysisJobId,
        IReadOnlyList<LlmReviewResult> reviews,
        CancellationToken ct = default)
    {
        if (reviews.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        var parameters = new
        {
            ReviewIds          = reviews.Select(r => r.ReviewId).ToArray(),
            AnalysisJobIds     = Enumerable.Repeat(analysisJobId, reviews.Count).ToArray(),
            OverallSentiments  = reviews.Select(r => r.OverallSentiment).ToArray(),
            OverallConfidences = reviews.Select(r => r.OverallConfidence).ToArray(),
            AspectsJson        = reviews.Select(r => JsonSerializer.Serialize(
                r.Aspects.Select(a => new ReviewAspect
                {
                    Topic = a.Topic,
                    Sentiment = a.Sentiment,
                    Confidence = a.Confidence,
                    Fragment = a.Fragment ?? "",
                    IsFreeform = a.IsFreeform
                }).ToList(),
                AspectsJsonOptions)).ToArray()
        };

        return await conn.ExecuteAsync(new CommandDefinition(Sql, parameters, cancellationToken: ct));
    }
}
