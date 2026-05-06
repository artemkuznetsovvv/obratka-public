using System.Text.Json;
using Dapper;
using ProcessingGateway.Application.Llm;

namespace ProcessingGateway.Infrastructure.Database;

/// Записывает `analysis_recommendations` для job-а (schema 2.0).
///
/// Стратегия — **DELETE + bulk INSERT** в одной транзакции. Это проще, чем upsert по
/// содержимому, и идемпотентно при replay (тот же `LlmResultMessage` приходит повторно):
/// старые строки уходят, на их место кладутся новые. Если LLM передоумал — таблица
/// отражает последний ответ.
///
/// `evidence` — JSONB; пишется как сериализованный list&lt;string&gt; в snake_case
/// (тот же JsonOptions, что в `ProcessingDbContext`).
public sealed class AnalysisRecommendationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IDbConnectionFactory _factory;

    public AnalysisRecommendationWriter(IDbConnectionFactory factory) => _factory = factory;

    private const string DeleteSql = @"
        DELETE FROM analysis_recommendations WHERE analysis_job_id = @JobId;";

    private const string InsertSql = @"
        INSERT INTO analysis_recommendations
            (analysis_job_id, priority, topic, title, body, expected_impact,
             evidence, sort_order, created_at)
        SELECT
            analysis_job_id, priority, topic, title, body, expected_impact,
            evidence::jsonb, sort_order, NOW()
        FROM UNNEST(
            @JobIds, @Priorities, @Topics, @Titles, @Bodies, @Impacts,
            @EvidenceJson, @SortOrders
        ) AS t(
            analysis_job_id, priority, topic, title, body, expected_impact,
            evidence, sort_order
        );";

    public async Task<int> ReplaceAllAsync(
        Guid analysisJobId,
        IReadOnlyList<LlmRecommendation> recommendations,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            DeleteSql, new { JobId = analysisJobId }, transaction: tx, cancellationToken: ct));

        if (recommendations.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var parameters = new
        {
            JobIds      = Enumerable.Repeat(analysisJobId, recommendations.Count).ToArray(),
            Priorities  = recommendations.Select(r => (short)r.Priority).ToArray(),
            Topics      = recommendations.Select(r => r.Topic).ToArray(),
            Titles      = recommendations.Select(r => r.Title).ToArray(),
            Bodies      = recommendations.Select(r => r.Body).ToArray(),
            Impacts     = recommendations.Select(r => (string?)r.ExpectedImpact).ToArray(),
            EvidenceJson = recommendations.Select(r => JsonSerializer.Serialize(
                r.Evidence ?? Array.Empty<string>(), JsonOptions)).ToArray(),
            SortOrders  = recommendations.Select((_, i) => i).ToArray()
        };

        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            InsertSql, parameters, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return inserted;
    }
}
