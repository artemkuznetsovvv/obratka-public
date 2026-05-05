using System.Text.Json;
using Dapper;
using ProcessingGateway.Application.Llm;

namespace ProcessingGateway.Infrastructure.Database;

/// Bulk-INSERT LLM-результатов в `review_llm_results`. Идемпотентность через UNIQUE
/// `(review_id, analysis_job_id)` + `ON CONFLICT DO NOTHING` — при повторной доставке
/// `LlmResultMessage` от LLM (или брокера) дубль молча отбрасывается.
///
/// `fake_reason_tags` и `topics` — JSONB, передаются через text-сериализацию + cast `::jsonb`,
/// иначе Dapper не понимает сложные параметры в jsonb-колонке.
public sealed class ReviewLlmResultBulkInserter
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IDbConnectionFactory _factory;

    public ReviewLlmResultBulkInserter(IDbConnectionFactory factory) => _factory = factory;

    private const string Sql = @"
        INSERT INTO review_llm_results
            (review_id, analysis_job_id, fake_status, fake_reason_tags,
             sentiment, sentiment_confidence, is_spam, spam_confidence, topics, processed_at)
        SELECT
            review_id, analysis_job_id, fake_status, fake_reason_tags::jsonb,
            sentiment, sentiment_confidence, is_spam, spam_confidence, topics::jsonb, NOW()
        FROM UNNEST(
            @ReviewIds, @AnalysisJobIds, @FakeStatuses, @FakeReasonTagsJson,
            @Sentiments, @SentimentConfidences, @IsSpams, @SpamConfidences, @TopicsJson
        ) AS t(
            review_id, analysis_job_id, fake_status, fake_reason_tags,
            sentiment, sentiment_confidence, is_spam, spam_confidence, topics
        )
        ON CONFLICT (review_id, analysis_job_id) DO NOTHING;";

    public async Task<int> InsertAsync(
        Guid analysisJobId,
        IReadOnlyList<LlmProcessedReview> processed,
        CancellationToken ct = default)
    {
        if (processed.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        var parameters = new
        {
            ReviewIds            = processed.Select(p => p.ReviewId).ToArray(),
            AnalysisJobIds       = Enumerable.Repeat(analysisJobId, processed.Count).ToArray(),
            FakeStatuses         = processed.Select(p => p.FakeStatus).ToArray(),
            FakeReasonTagsJson   = processed.Select(p => JsonSerializer.Serialize(p.FakeReasonTags, JsonOptions)).ToArray(),
            Sentiments           = processed.Select(p => p.Sentiment).ToArray(),
            SentimentConfidences = processed.Select(p => p.SentimentConfidence).ToArray(),
            IsSpams              = processed.Select(p => p.IsSpam).ToArray(),
            SpamConfidences      = processed.Select(p => p.SpamConfidence).ToArray(),
            TopicsJson           = processed.Select(p => JsonSerializer.Serialize(p.Topics, JsonOptions)).ToArray()
        };

        return await conn.ExecuteAsync(new CommandDefinition(Sql, parameters, cancellationToken: ct));
    }
}
