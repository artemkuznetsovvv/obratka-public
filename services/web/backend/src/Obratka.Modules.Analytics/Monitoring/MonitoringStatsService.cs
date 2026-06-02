using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;

namespace Obratka.Modules.Analytics.Monitoring;

// Read-only расчёты для live-мониторинга поверх processing_db (analytics_reader).
// «Растущий seed-job»: все отзивы мониторинга живут под одним analysis_job_id; срез цикла
// режем по reviews.collected_at >= cycleStart (момент старта цикла, заданный Web API).
public sealed record MonitoringCycleStats(
    int NewReviewCount,
    int TotalReviews,
    double NegativeRatioPp);

public interface IMonitoringStatsService
{
    Task<MonitoringCycleStats> ComputeCycleStatsAsync(
        Guid seedJobId, DateTimeOffset cycleStart, CancellationToken ct);
}

internal sealed class MonitoringStatsService(ProcessingReadContext db) : IMonitoringStatsService
{
    // schema 2.0: русские литералы; пустой sentiment исключаем (конвенция метрики M3).
    private const string Negative = "негативный";

    public async Task<MonitoringCycleStats> ComputeCycleStatsAsync(
        Guid seedJobId, DateTimeOffset cycleStart, CancellationToken ct)
    {
        // Все отзывы seed-job (через M2M analysis_job_reviews).
        var total = await db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == seedJobId)
            .CountAsync(ct);

        // Новые за цикл: отзывы seed-job, заингесченные в окне цикла.
        var newReviews = db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == seedJobId)
            .Join(db.Reviews, ajr => ajr.ReviewId, r => r.Id, (_, r) => r)
            .Where(r => r.CollectedAt >= cycleStart);

        var newCount = await newReviews.CountAsync(ct);

        // Доля «негативный» по срезу новых отзывов (JOIN на llm по review_id в рамках seed-job).
        var slice = await newReviews
            .Join(
                db.ReviewLlmResults.Where(l => l.AnalysisJobId == seedJobId && l.OverallSentiment != ""),
                r => r.Id, l => l.ReviewId, (_, l) => l.OverallSentiment)
            .GroupBy(s => s)
            .Select(g => new { Sentiment = g.Key, Count = g.LongCount() })
            .ToListAsync(ct);

        long negative = slice.Where(x => x.Sentiment == Negative).Sum(x => x.Count);
        long totalNonEmpty = slice.Sum(x => x.Count);
        double negPp = totalNonEmpty == 0 ? 0 : (double)negative / totalNonEmpty * 100.0;

        return new MonitoringCycleStats(newCount, total, negPp);
    }
}
