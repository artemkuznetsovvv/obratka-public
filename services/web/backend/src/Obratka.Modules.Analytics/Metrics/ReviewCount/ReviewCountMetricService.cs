using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Metrics.ReviewCount;

public interface IReviewCountMetricService
{
    Task<ReviewCountMetricResult> ComputeAsync(
        ReviewCountMetricQuery query, CancellationToken ct);
}

// Параметры запроса метрики 1 / О1.
// BranchIds — обязательный непустой набор: для М1 (per-branch) приходит один id,
// для О1 (по сети) — все выбранные в фильтре. Пустой список = 400 на endpoint.
//
// Sentiments / Stars: null или пустой массив = «фильтр не применять» (все значения).
// Это сделано чтобы пустой select на UI не сворачивал данные в ноль — UX так понятнее.
public sealed record ReviewCountMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sentiments,
    IReadOnlyCollection<short>? Stars);

public sealed record ReviewCountMetricResult(
    long TotalCurrent,
    long TotalPrevious,
    bool HasPreviousPeriod,
    IReadOnlyDictionary<string, SourceCounts> BySource);

public sealed record SourceCounts(long Current, long Previous);

internal sealed class ReviewCountMetricService(ProcessingReadContext db)
    : IReviewCountMetricService
{
    // Источники, под которые карточка ВСЕГДА отдаёт строку (спека: «3 строки
    // всегда, снятые в фильтре = 0»). Если в будущем появится новый источник —
    // добавляем сюда.
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];

    public async Task<ReviewCountMetricResult> ComputeAsync(
        ReviewCountMetricQuery q, CancellationToken ct)
    {
        // Семантика дат фильтра: from inclusive (начало дня), to inclusive
        // («1-30 апреля» означает оба дня включительно). В SQL это переводим
        // как полуоткрытый интервал [from, to+1day): дата 30 апреля попадает,
        // дата 1 мая — нет. Иначе с `< to` весь последний день выпадал бы.
        //
        // Расчёт «предыдущего периода»: длительность равна выбранному, кончается
        // за день до начала текущего. Спека: «1-30 апреля → 2-31 марта».
        //   duration = (to+1day) - from = 30 дней
        //   prev_to_exclusive = from = 1 апреля 00:00
        //   prev_from = from - duration = 2 марта 00:00
        //   → prev [2 марта, 1 апреля) = 2-31 марта включительно. ✓
        var hasPrevious = q.From is not null && q.To is not null;
        DateTimeOffset? toExclusive = q.To?.AddDays(1);
        DateTimeOffset? prevFromInclusive = null, prevToExclusive = null;
        if (hasPrevious)
        {
            var duration = toExclusive!.Value - q.From!.Value;
            prevToExclusive = q.From.Value;
            prevFromInclusive = q.From.Value - duration;
        }

        // Базовый набор: reviews джоба × выбранные branches.
        // JOIN на analysis_job_reviews даёт jobId-скоупинг (reviews шарятся между
        // job'ами одной компании). Contains() → SQL IN(...). JOIN на
        // review_llm_results нужен ТОЛЬКО когда фильтр sentiments активен
        // (иначе INNER JOIN бы исключал отзывы без LLM-результата).
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

        if (q.Stars is { Count: > 0 })
        {
            var starsList = q.Stars.ToList();
            baseQuery = baseQuery.Where(r => r.Stars != null && starsList.Contains(r.Stars.Value));
        }

        // Если фильтр тональности активен — JOIN на review_llm_results и фильтр
        // по overall_sentiment. Один отзыв → один результат для конкретного job-а
        // (LLM может перезаписать при replay, но всегда последний живой).
        IQueryable<Review> filtered = baseQuery;
        if (q.Sentiments is { Count: > 0 })
        {
            var sentList = q.Sentiments.ToList();
            filtered = baseQuery.Join(
                db.ReviewLlmResults.Where(llm => llm.AnalysisJobId == q.JobId
                                                  && sentList.Contains(llm.OverallSentiment)),
                r => r.Id,
                llm => llm.ReviewId,
                (r, _) => r);
        }

        // Один запрос отдаёт сразу current и previous counts per source через
        // CASE WHEN. Сервер PG считает оба окна за один проход по индексу
        // (branch_id, review_date). Гораздо дешевле, чем 2 отдельных GROUP BY.
        var grouped = await filtered
            .GroupBy(r => r.Source)
            .Select(g => new
            {
                Source = g.Key,
                Current = g.Count(r =>
                    (q.From == null || r.ReviewDate >= q.From) &&
                    (toExclusive == null || r.ReviewDate < toExclusive)),
                Previous = g.Count(r =>
                    prevFromInclusive != null && prevToExclusive != null
                    && r.ReviewDate >= prevFromInclusive && r.ReviewDate < prevToExclusive),
            })
            .ToListAsync(ct);

        // Заполняем фиксированный набор источников: если в БД нет какого-то
        // источника — выдаём (0, 0). Прочие источники, не входящие в FixedSources
        // (если вдруг появятся в БД), на этом этапе игнорируются — карточка их
        // не отображает.
        var byMap = grouped.ToDictionary(x => x.Source, x => new SourceCounts(x.Current, x.Previous));
        var bySource = new Dictionary<string, SourceCounts>(FixedSources.Length);
        foreach (var src in FixedSources)
        {
            bySource[src] = byMap.TryGetValue(src, out var c) ? c : new SourceCounts(0, 0);
        }

        var totalCurrent = bySource.Values.Sum(x => x.Current);
        var totalPrevious = hasPrevious ? bySource.Values.Sum(x => x.Previous) : 0;

        return new ReviewCountMetricResult(
            TotalCurrent: totalCurrent,
            TotalPrevious: totalPrevious,
            HasPreviousPeriod: hasPrevious,
            BySource: bySource);
    }
}
