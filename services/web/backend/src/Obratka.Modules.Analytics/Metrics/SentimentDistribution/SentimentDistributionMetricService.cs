using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;

namespace Obratka.Modules.Analytics.Metrics.SentimentDistribution;

public interface ISentimentDistributionMetricService
{
    Task<SentimentDistributionResult> ComputeAsync(
        SentimentDistributionQuery query, CancellationToken ct);
}

// Метрика М3 / О3 «Настроение клиентов».
// ВАЖНОЕ исключение из общего контракта фильтров: фильтр Sentiments СЮДА не
// передаётся — метрика сама показывает распределение по 3 sentiments-значениям,
// сужение фильтром этого среза сломало бы UX (распределение перестало бы
// суммироваться в 100%). По той же причине эта карточка не возвращает «empty»
// records — пустые overall_sentiment ('') исключены из числителя и знаменателя
// (см. спеку М3 «Исключение из расчёта»).
//
// Sources фильтр применяется на сервере (нет per-source декомпозиции в М3,
// фильтр просто сужает выборку).
public sealed record SentimentDistributionQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars);

// Counts для 3 «полезных» категорий + total. Фронт сам считает проценты и
// применяет таблицу правил «фраза-вывод». Возвращаем абсолютные counts (counts
// vs ratios — ADR-003 §«Counts > ratios», в т.ч. чтобы суммировать по нескольким
// branch'ам если когда-нибудь будет иерархия).
public sealed record SentimentDistributionResult(
    long Positive,
    long Neutral,
    long Negative,
    long TotalNonEmpty);

internal sealed class SentimentDistributionMetricService(ProcessingReadContext db)
    : ISentimentDistributionMetricService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;

    // Значения sentiment в schema 2.0 — русские строки. Пустую строку
    // явно отфильтровываем в SQL (overall_sentiment != '').
    private const string Positive = "позитивный";
    private const string Neutral = "нейтральный";
    private const string Negative = "негативный";

    public async Task<SentimentDistributionResult> ComputeAsync(
        SentimentDistributionQuery q, CancellationToken ct)
    {
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

        // Stars: фильтруем только если выбран неполный поднабор.
        if (q.Stars is { Count: > 0 } stars && stars.Count < AllStarsCount)
        {
            var starsList = stars.ToList();
            baseQuery = baseQuery.Where(r => r.Stars != null && starsList.Contains(r.Stars.Value));
        }

        // Sources: фильтруем только если выбран неполный набор из 3 фиксированных.
        if (q.Sources is { Count: > 0 } src
            && !(src.Count == FixedSources.Length && FixedSources.All(s => src.Contains(s))))
        {
            var sourcesList = src.ToList();
            baseQuery = baseQuery.Where(r => sourcesList.Contains(r.Source));
        }

        // Окно периода (from inclusive, to inclusive через +1day).
        if (q.From is { } from)
            baseQuery = baseQuery.Where(r => r.ReviewDate >= from);
        if (toExclusive is { } toEx)
            baseQuery = baseQuery.Where(r => r.ReviewDate < toEx);

        // INNER JOIN на review_llm_results + исключаем пустые sentiment'ы.
        // Пустые исключены ДО подсчёта — чтобы не раздувать «нейтральный» (см. спеку).
        var grouped = await baseQuery
            .Join(
                db.ReviewLlmResults.Where(llm => llm.AnalysisJobId == q.JobId
                                                  && llm.OverallSentiment != ""),
                r => r.Id,
                llm => llm.ReviewId,
                (_, llm) => llm.OverallSentiment)
            .GroupBy(s => s)
            .Select(g => new { Sentiment = g.Key, Count = g.LongCount() })
            .ToListAsync(ct);

        long positive = 0, neutral = 0, negative = 0;
        foreach (var x in grouped)
        {
            switch (x.Sentiment)
            {
                case Positive: positive = x.Count; break;
                case Neutral:  neutral  = x.Count; break;
                case Negative: negative = x.Count; break;
                // Прочие неожиданные значения молча игнорируем (защита от
                // расхождений schema-версий LLM-контракта).
            }
        }

        return new SentimentDistributionResult(
            Positive: positive,
            Neutral: neutral,
            Negative: negative,
            TotalNonEmpty: positive + neutral + negative);
    }
}
