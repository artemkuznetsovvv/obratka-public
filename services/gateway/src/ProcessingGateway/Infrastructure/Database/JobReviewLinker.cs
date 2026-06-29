using Dapper;

namespace ProcessingGateway.Infrastructure.Database;

/// Связывает только что заингеженные отзывы с конкретным `analysis_job`-ом.
/// Решает «какие отзывы относились к этому анализу» — `RawReviewBulkInserter`
/// делает `ON CONFLICT DO NOTHING` (старые отзывы остаются с прежним id и НЕ
/// перезаписываются), а здесь мы по `composite_key`-ам поднимаем их id и
/// записываем строки `analysis_job_reviews`. Идемпотентно: повторный вызов
/// с теми же входами даёт 0 вставок.
public sealed class JobReviewLinker
{
    private readonly IDbConnectionFactory _factory;

    public JobReviewLinker(IDbConnectionFactory factory) => _factory = factory;

    private const string Sql = @"
        INSERT INTO analysis_job_reviews (analysis_job_id, review_id)
        SELECT @JobId, r.id
        FROM reviews r
        WHERE r.composite_key = ANY(@CompositeKeys)
        ON CONFLICT (analysis_job_id, review_id) DO NOTHING;";

    public async Task<int> LinkAsync(Guid jobId, IReadOnlyCollection<string> compositeKeys, CancellationToken ct = default)
    {
        if (compositeKeys.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(Sql, new
        {
            JobId = jobId,
            CompositeKeys = compositeKeys.ToArray()
        }, cancellationToken: ct));
    }
}
