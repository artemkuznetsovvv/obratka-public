using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Obratka.Modules.Analytics.Data;

namespace Obratka.Modules.Analytics.Metrics.TopTopics;

public interface ITopTopicsMetricService
{
    Task<TopTopicsMetricResult> ComputeAsync(TopTopicsMetricQuery query, CancellationToken ct);
}

// Метрика М5 «О чём говорят чаще всего» — топ-3 темы по числу отзывов, в
// которых они упомянуты + распределение pos/neg внутри темы.
//
// Принимает обычный набор фильтров: branch, period, sources, stars.
// Sentiments — НЕ принимаем (метрика про разрез по тональности внутри тем,
// сужение по sentiments сломало бы pos/neg-counts).
public sealed record TopTopicsMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars);

public sealed record TopTopicsMetricResult(
    IReadOnlyList<TopicAggregate> Topics,
    long TotalReviewsInPeriod);

// Не record-positional — потому что EF Core SqlQueryRaw маппит по property name
// (для positional record нужны "_1, _2..." имена либо ConstructorBinding).
// Простой класс с init-сеттерами читается так же чисто и работает гарантированно.
public sealed class TopicAggregate
{
    public string Topic { get; init; } = string.Empty;
    public long ReviewCount { get; init; }       // отзыв засчитан в тему один раз
    public long PositiveMentions { get; init; }  // mentions с sentiment=позитивный
    public long NegativeMentions { get; init; }  // mentions с sentiment=негативный
}

internal sealed class TopTopicsMetricService(ProcessingReadContext db)
    : ITopTopicsMetricService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;
    private const int TopN = 3;

    public async Task<TopTopicsMetricResult> ComputeAsync(
        TopTopicsMetricQuery q, CancellationToken ct)
    {
        var toExclusive = q.To?.AddDays(1);
        var branchIds = q.BranchIds.ToArray();

        // 1) Топ-3 темы — raw SQL потому что в EF LINQ нет аналога
        // CROSS JOIN LATERAL jsonb_array_elements. Используем GIN-индекс
        // ix_review_llm_results_aspects_gin (упомянут в спеке).
        var (sql, parameters) = BuildTopicsSql(q, branchIds, toExclusive);
        var topics = await db.Database
            .SqlQueryRaw<TopicAggregate>(sql, parameters.Cast<object>().ToArray())
            .ToListAsync(ct);

        // 2) Общее число отзывов за период по выбранному филиалу — для расчёта
        // доли темы. Спека: «доля от общего числа отзывов за период», т.е.
        // делим на ВСЕ отзывы, а не только на те у которых есть aspects.
        var totalQuery = db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == q.JobId)
            .Join(db.Reviews, ajr => ajr.ReviewId, r => r.Id, (_, r) => r)
            .Where(r => branchIds.Contains(r.BranchId));

        if (q.Stars is { Count: > 0 } stars && stars.Count < AllStarsCount)
        {
            var starsList = stars.ToList();
            totalQuery = totalQuery.Where(r => r.Stars != null && starsList.Contains(r.Stars.Value));
        }
        if (q.Sources is { Count: > 0 } src
            && !(src.Count == FixedSources.Length && FixedSources.All(s => src.Contains(s))))
        {
            var sourcesList = src.ToList();
            totalQuery = totalQuery.Where(r => sourcesList.Contains(r.Source));
        }
        if (q.From is { } from)
            totalQuery = totalQuery.Where(r => r.ReviewDate >= from);
        if (toExclusive is { } toEx)
            totalQuery = totalQuery.Where(r => r.ReviewDate < toEx);

        var totalReviews = await totalQuery.CountAsync(ct);

        return new TopTopicsMetricResult(topics, totalReviews);
    }

    private static (string Sql, List<NpgsqlParameter> Parameters) BuildTopicsSql(
        TopTopicsMetricQuery q,
        Guid[] branchIds,
        DateTimeOffset? toExclusive)
    {
        var sb = new StringBuilder(@"
            SELECT
                elem->>'topic'                                              AS ""Topic"",
                COUNT(DISTINCT r.id)                                        AS ""ReviewCount"",
                COUNT(*) FILTER (WHERE elem->>'sentiment' = 'позитивный')   AS ""PositiveMentions"",
                COUNT(*) FILTER (WHERE elem->>'sentiment' = 'негативный')   AS ""NegativeMentions""
            FROM analysis_job_reviews ajr
            JOIN reviews r              ON r.id = ajr.review_id
            JOIN review_llm_results llm ON llm.review_id = r.id
                                        AND llm.analysis_job_id = ajr.analysis_job_id
            CROSS JOIN LATERAL jsonb_array_elements(llm.aspects) AS elem
            WHERE ajr.analysis_job_id = @p_job_id
              AND r.branch_id = ANY(@p_branch_ids)
              AND elem->>'topic' IS NOT NULL
              AND elem->>'topic' <> ''
        ");

        var parameters = new List<NpgsqlParameter>
        {
            new("p_job_id", q.JobId),
            new("p_branch_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = branchIds },
        };

        if (q.From is { } from)
        {
            sb.Append(" AND r.review_date >= @p_from");
            parameters.Add(new("p_from", from));
        }
        if (toExclusive is { } toEx)
        {
            sb.Append(" AND r.review_date < @p_to_excl");
            parameters.Add(new("p_to_excl", toEx));
        }

        if (q.Sources is { Count: > 0 } src
            && !(src.Count == FixedSources.Length && FixedSources.All(s => src.Contains(s))))
        {
            sb.Append(" AND r.source = ANY(@p_sources)");
            parameters.Add(new("p_sources", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = src.ToArray(),
            });
        }

        if (q.Stars is { Count: > 0 } stars && stars.Count < AllStarsCount)
        {
            sb.Append(" AND r.stars = ANY(@p_stars)");
            parameters.Add(new("p_stars", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            {
                Value = stars.ToArray(),
            });
        }

        sb.Append(@"
            GROUP BY elem->>'topic'
            ORDER BY ""ReviewCount"" DESC
            LIMIT ").Append(TopN);

        return (sb.ToString(), parameters);
    }
}
