using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;

namespace Obratka.Modules.Analytics.Metrics.SentimentDistribution;

public interface ISentimentReviewsService
{
    Task<SentimentReviewsResult> ListAsync(SentimentReviewsQuery query, CancellationToken ct);
}

// «Top reviews по конкретному sentiment'у» — для модалки раскрытия М3/О3.
// ADR-003 явно разрешает одно raw-обращение к reviews × review_llm_results
// (см. §«Топ-N»): запрос лёгкий, индекс по analysis_job_id + сортировка
// review_date DESC, LIMIT.
//
// Параметры зеркалят SentimentDistribution + поле Sentiment (одно значение) +
// limit/offset для пагинации. Sources/Stars фильтруются на сервере (как в М3).
public sealed record SentimentReviewsQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    string Sentiment,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars,
    int Limit,
    int Offset);

public sealed record SentimentReviewsResult(
    IReadOnlyList<SentimentReviewItem> Items,
    bool HasMore);

public sealed record SentimentReviewItem(
    long Id,
    string Source,
    DateTimeOffset ReviewDate,
    short? Stars,
    string Text);

internal sealed class SentimentReviewsService(ProcessingReadContext db)
    : ISentimentReviewsService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;
    private const int MaxLimit = 200;

    public async Task<SentimentReviewsResult> ListAsync(
        SentimentReviewsQuery q, CancellationToken ct)
    {
        // Защита от слишком большого payload — отзывы могут быть длинные.
        // 200 хватит на самый детальный просмотр; для большего объёма — pagination.
        var limit = Math.Min(Math.Max(q.Limit, 1), MaxLimit);
        var offset = Math.Max(q.Offset, 0);

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

        if (q.From is { } from)
            baseQuery = baseQuery.Where(r => r.ReviewDate >= from);
        if (toExclusive is { } toEx)
            baseQuery = baseQuery.Where(r => r.ReviewDate < toEx);

        var sentiment = q.Sentiment;
        var filtered = baseQuery.Join(
            db.ReviewLlmResults.Where(llm => llm.AnalysisJobId == q.JobId
                                              && llm.OverallSentiment == sentiment),
            r => r.Id,
            llm => llm.ReviewId,
            (r, _) => r);

        // limit+1 — индикатор «есть ещё», без отдельного COUNT (дешевле, точнее
        // когда между page'ами реально появляются новые отзывы — для нас этого
        // не случится, но паттерн пусть будет правильным).
        var rows = await filtered
            .OrderByDescending(r => r.ReviewDate)
            .ThenByDescending(r => r.Id)
            .Skip(offset)
            .Take(limit + 1)
            .Select(r => new SentimentReviewItem(r.Id, r.Source, r.ReviewDate, r.Stars, r.RawText))
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new SentimentReviewsResult(rows, hasMore);
    }
}
