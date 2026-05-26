using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;

namespace Obratka.Modules.Analytics.Metrics.FreshPulse;

public interface IFreshPulseMetricService
{
    Task<FreshPulseMetricResult> ComputeAsync(
        FreshPulseMetricQuery query, CancellationToken ct);
}

// Метрика М4 «Свежий пульс». Окно — последние 30 дней от now (на UI period
// не выбирается), плюс параллельное окно [-60d, -30d) для динамики.
//
// Исключения из общего контракта фильтров:
//   - Sentiments-фильтр НЕ принимается (метрика сама про sentiments).
//   - Period (фильтр дашборда) ИГНОРИРУЕТСЯ — окно жёстко 30 дней.
// Применяются: branch, sources, stars.
public sealed record FreshPulseMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars);

public sealed record FreshPulseMetricResult(
    FreshPulseWindowResult Current,
    FreshPulseWindowResult Previous);

// Index = (positive - negative) / totalNonEmpty * 100. Range [-100, +100].
// totalNonEmpty=0 → Index=null (UI рисует «Нет данных» либо «—» для prev).
public sealed record FreshPulseWindowResult(
    double? Index,
    long Positive,
    long Neutral,
    long Negative,
    long TotalNonEmpty,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive);

internal sealed class FreshPulseMetricService(ProcessingReadContext db)
    : IFreshPulseMetricService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;
    private const int WindowDays = 30;

    private const string Positive = "позитивный";
    private const string Neutral = "нейтральный";
    private const string Negative = "негативный";

    public async Task<FreshPulseMetricResult> ComputeAsync(
        FreshPulseMetricQuery q, CancellationToken ct)
    {
        // Now на сервере (UTC). DateTimeOffset.UtcNow прямо — TimeProvider
        // через DI нужен только когда есть тесты с подменой времени.
        var now = DateTimeOffset.UtcNow;
        var currentTo = now;
        var currentFrom = now.AddDays(-WindowDays);
        var previousTo = currentFrom;
        var previousFrom = previousTo.AddDays(-WindowDays);

        var branchIds = q.BranchIds.ToList();

        // Базовый фильтр (branch + sources + stars) применяется одинаково
        // к обоим окнам. Period дашборда сюда не передаётся.
        IQueryable<long> BaseReviewIds(DateTimeOffset from, DateTimeOffset toExclusive)
        {
            var baseQuery = db.AnalysisJobReviews
                .AsNoTracking()
                .Where(ajr => ajr.AnalysisJobId == q.JobId)
                .Join(
                    db.Reviews,
                    ajr => ajr.ReviewId,
                    r => r.Id,
                    (ajr, r) => r)
                .Where(r => branchIds.Contains(r.BranchId))
                .Where(r => r.ReviewDate >= from && r.ReviewDate < toExclusive);

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

            return baseQuery.Select(r => r.Id);
        }

        async Task<FreshPulseWindowResult> ComputeWindow(DateTimeOffset from, DateTimeOffset toExclusive)
        {
            var reviewIds = BaseReviewIds(from, toExclusive);

            // INNER JOIN на review_llm_results + исключение overall_sentiment=''.
            // Counts per sentiment одним запросом.
            //
            // FRESHNESS-WEIGHTS TODO (OBR-35): когда в БД появится поле веса
            // (reviews.freshness_score или эквивалент), нужно заменить COUNT(*)
            // на SUM(weight). Сейчас все веса = 1, формула эквивалентна
            // равномерному.
            var counts = await db.ReviewLlmResults
                .AsNoTracking()
                .Where(llm => llm.AnalysisJobId == q.JobId
                              && llm.OverallSentiment != ""
                              && reviewIds.Contains(llm.ReviewId))
                .GroupBy(llm => llm.OverallSentiment)
                .Select(g => new { Sentiment = g.Key, Count = g.LongCount() })
                .ToListAsync(ct);

            long positive = 0, neutral = 0, negative = 0;
            foreach (var c in counts)
            {
                switch (c.Sentiment)
                {
                    case Positive: positive = c.Count; break;
                    case Neutral:  neutral  = c.Count; break;
                    case Negative: negative = c.Count; break;
                }
            }

            var total = positive + neutral + negative;
            double? index = total == 0
                ? null
                : (double)(positive - negative) / total * 100.0;

            return new FreshPulseWindowResult(
                Index: index,
                Positive: positive,
                Neutral: neutral,
                Negative: negative,
                TotalNonEmpty: total,
                FromInclusive: from,
                ToExclusive: toExclusive);
        }

        // Два окна последовательно — каждое лёгкое (агрегат с индексом по
        // (branch_id, review_date)), параллелизм с одним DbContext'ом проблемы
        // не стоит. Если позже захочется ускорить — можно сделать одним SQL
        // через CASE WHEN, как в М1.
        var current = await ComputeWindow(currentFrom, currentTo);
        var previous = await ComputeWindow(previousFrom, previousTo);

        return new FreshPulseMetricResult(current, previous);
    }
}
