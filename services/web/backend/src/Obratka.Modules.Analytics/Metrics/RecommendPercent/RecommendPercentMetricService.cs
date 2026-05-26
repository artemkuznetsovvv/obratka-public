using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Metrics.RecommendPercent;

public interface IRecommendPercentMetricService
{
    Task<RecommendPercentMetricResult> ComputeAsync(
        RecommendPercentMetricQuery query, CancellationToken ct);
}

// Метрика М6 «Сколько клиентов рекомендуют» — доля overall_sentiment=позитивный
// от total non-empty за период по выбранному филиалу.
//
// Принимает: branch, period, sources, stars. Sentiments — НЕ принимаем
// (метрика буквально про долю одного из значений).
public sealed record RecommendPercentMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars);

// Counts → фронт считает % сам. Symmetric to М1: previous доступен только
// когда обе границы периода заданы (иначе hasPrevious=false → тренд «—»).
public sealed record RecommendPercentMetricResult(
    RecommendPercentWindow Current,
    RecommendPercentWindow Previous,
    bool HasPreviousPeriod);

public sealed record RecommendPercentWindow(
    long Positive,
    long TotalNonEmpty);

internal sealed class RecommendPercentMetricService(ProcessingReadContext db)
    : IRecommendPercentMetricService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;
    private const string PositiveSentiment = "позитивный";

    public async Task<RecommendPercentMetricResult> ComputeAsync(
        RecommendPercentMetricQuery q, CancellationToken ct)
    {
        // Границы как в М1 (см. ReviewCountMetricService): from inclusive, to inclusive
        // (toExclusive = to + 1 day). Previous = period той же длительности
        // прямо перед current.
        var hasPrevious = q.From is not null && q.To is not null;
        var toExclusive = q.To?.AddDays(1);
        DateTimeOffset? prevFromInclusive = null, prevToExclusive = null;
        if (hasPrevious)
        {
            var duration = toExclusive!.Value - q.From!.Value;
            prevToExclusive = q.From.Value;
            prevFromInclusive = q.From.Value - duration;
        }

        var current = await ComputeWindow(q, q.From, toExclusive, ct);
        var previous = hasPrevious
            ? await ComputeWindow(q, prevFromInclusive, prevToExclusive, ct)
            : new RecommendPercentWindow(0, 0);

        return new RecommendPercentMetricResult(current, previous, hasPrevious);
    }

    private async Task<RecommendPercentWindow> ComputeWindow(
        RecommendPercentMetricQuery q,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toExclusive,
        CancellationToken ct)
    {
        var branchIds = q.BranchIds.ToList();

        IQueryable<Review> baseQuery = db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == q.JobId)
            .Join(db.Reviews, ajr => ajr.ReviewId, r => r.Id, (_, r) => r)
            .Where(r => branchIds.Contains(r.BranchId));

        if (fromInclusive is { } from)
            baseQuery = baseQuery.Where(r => r.ReviewDate >= from);
        if (toExclusive is { } toEx)
            baseQuery = baseQuery.Where(r => r.ReviewDate < toEx);

        if (q.Stars is { Count: > 0 } stars && stars.Count < AllStarsCount)
        {
            var starsList = stars.ToList();
            baseQuery = baseQuery.Where(r => r.Stars != null && starsList.Contains(r.Stars.Value));
        }
        if (q.Sources is { Count: > 0 } src
            && !(src.Count == FixedSources.Length && FixedSources.All(s => src.Contains(s))))
        {
            var sourcesList = src.ToList();
            baseQuery = baseQuery.Where(r => sourcesList.Contains(r.Source));
        }

        // INNER JOIN на llm + исключение overall_sentiment='' (по спеке: «не
        // учитываются ни в числителе, ни в знаменателе»). Возвращаем
        // (positive, total) одним агрегатом на сервере.
        var aggregate = await baseQuery
            .Join(
                db.ReviewLlmResults.Where(llm => llm.AnalysisJobId == q.JobId
                                                  && llm.OverallSentiment != ""),
                r => r.Id,
                llm => llm.ReviewId,
                (_, llm) => llm.OverallSentiment)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Positive = g.LongCount(s => s == PositiveSentiment),
                Total = g.LongCount(),
            })
            .FirstOrDefaultAsync(ct);

        return aggregate is null
            ? new RecommendPercentWindow(0, 0)
            : new RecommendPercentWindow(aggregate.Positive, aggregate.Total);
    }
}
