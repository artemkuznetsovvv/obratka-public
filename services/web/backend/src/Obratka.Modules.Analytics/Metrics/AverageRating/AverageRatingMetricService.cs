using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Metrics.AverageRating;

public interface IAverageRatingMetricService
{
    Task<AverageRatingMetricResult> ComputeAsync(
        AverageRatingMetricQuery query, CancellationToken ct);
}

// Параметры — те же что у М1 для consistency (один и тот же контекст фильтров
// дашборда). Sentiments/Stars: пусто/полный набор = «не фильтрую» (см. М1
// commit 0c7a014).
public sealed record AverageRatingMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sentiments,
    IReadOnlyCollection<short>? Stars);

// TotalAverage / SourceAvg — null если нет ни одного отзыва со stars (по спеке
// «если 0 stars → "—"»). TotalCount / SourceCount нужны фронту чтобы:
//   а) понять отображать или скрыть подпись («если 0 отзывов вообще»),
//   б) при необходимости пересчитать «честный» weighted total локально по
//      выбранным в фильтре source'ам (как делает М1 для total).
public sealed record AverageRatingMetricResult(
    double? TotalAverage,
    int TotalCount,
    IReadOnlyDictionary<string, SourceAverage> BySource);

public sealed record SourceAverage(double? Average, int Count);

internal sealed class AverageRatingMetricService(ProcessingReadContext db)
    : IAverageRatingMetricService
{
    // Тот же фикс-набор источников что у М1 — карточка всегда показывает 3
    // мини-блока в порядке 2gis → yandex → google (см. спеку М2).
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];

    private static readonly HashSet<string> AllSentiments = new(StringComparer.Ordinal)
    {
        "позитивный",
        "нейтральный",
        "негативный",
    };

    private const int AllStarsCount = 5;

    public async Task<AverageRatingMetricResult> ComputeAsync(
        AverageRatingMetricQuery q, CancellationToken ct)
    {
        // Границы периода: from inclusive, to inclusive (toExclusive = to+1day).
        // Подробное обоснование — в ReviewCountMetricService. Тренда к предыдущему
        // периоду у М2 по спеке нет, prev-период не считаем.
        var toExclusive = q.To?.AddDays(1);

        var branchIds = q.BranchIds.ToList();
        var baseQuery = db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == q.JobId)
            .Join(
                db.Reviews,
                ajr => ajr.ReviewId,
                r => r.Id,
                (ajr, r) => r)
            .Where(r => branchIds.Contains(r.BranchId));

        // Stars: если выбраны не все 5 — сужаем. Иначе пропускаем (NULL stars
        // не теряем). NB: для метрики AVG это очевидно тавтологично сужает,
        // но контракт фильтров — единый по всему слою (зонтичная задача).
        if (q.Stars is { Count: > 0 } stars && stars.Count < AllStarsCount)
        {
            var starsList = stars.ToList();
            baseQuery = baseQuery.Where(r => r.Stars != null && starsList.Contains(r.Stars.Value));
        }

        IQueryable<Review> filtered = baseQuery;
        if (q.Sentiments is { Count: > 0 } sent
            && !(sent.Count == AllSentiments.Count && sent.All(AllSentiments.Contains)))
        {
            var sentList = sent.ToList();
            filtered = baseQuery.Join(
                db.ReviewLlmResults.Where(llm => llm.AnalysisJobId == q.JobId
                                                  && sentList.Contains(llm.OverallSentiment)),
                r => r.Id,
                llm => llm.ReviewId,
                (r, _) => r);
        }

        // Считаем AVG только по отзывам со stars (спека: «расчёт ведётся только
        // по отзывам, у которых stars не пустое»). Group by source — один запрос.
        var grouped = await filtered
            .Where(r => r.Stars != null && r.ReviewDate >= (q.From ?? DateTimeOffset.MinValue)
                        && (toExclusive == null || r.ReviewDate < toExclusive))
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                Source = g.Key,
                Average = g.Average(r => (double)r.Stars!.Value),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        // Фиксированные 3 источника. Прочие (если вдруг появятся) игнорируем.
        var byMap = grouped.ToDictionary(x => x.Source, x => new SourceAverage(x.Average, x.Count));
        var bySource = new Dictionary<string, SourceAverage>(FixedSources.Length);
        foreach (var src in FixedSources)
        {
            bySource[src] = byMap.TryGetValue(src, out var s) ? s : new SourceAverage(null, 0);
        }

        // Total = weighted average по 3 источникам. Эквивалентно AVG(stars) по
        // всему filtered.Where(stars IS NOT NULL) — не требует отдельного round-trip.
        var totalCount = bySource.Values.Sum(x => x.Count);
        double? totalAvg = totalCount == 0
            ? null
            : bySource.Values.Sum(x => (x.Average ?? 0) * x.Count) / totalCount;

        return new AverageRatingMetricResult(
            TotalAverage: totalAvg,
            TotalCount: totalCount,
            BySource: bySource);
    }
}
