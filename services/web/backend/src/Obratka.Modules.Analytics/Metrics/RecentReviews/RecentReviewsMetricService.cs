using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Metrics.RecentReviews;

public interface IRecentReviewsMetricService
{
    Task<RecentReviewsMetricResult> ComputeAsync(
        RecentReviewsMetricQuery query, CancellationToken ct);
}

// 5 окон по спеке M7. Месяцы — реальные (AddMonths), не «30/90/180/360 дней»,
// чтобы границы попадали на ту же дату что N месяцев назад.
public enum RecentReviewsWindow
{
    Days7,
    Days30,
    Months3,
    Months6,
    Months12,
}

// Метрика М7 «Новые отзывы за период».
// Period дашборда сюда не передаётся — окно выбирается переключателем на
// самой карточке. Sentiments тоже не передаётся — метрика чисто количественная.
// Применяются: branch, sources, stars.
public sealed record RecentReviewsMetricQuery(
    Guid JobId,
    IReadOnlyCollection<Guid> BranchIds,
    RecentReviewsWindow Window,
    IReadOnlyCollection<string>? Sources,
    IReadOnlyCollection<short>? Stars);

// Counts для current + 3 previous окон + индикатор истории.
// FullPreviousWindows ∈ {0, 1, 2, 3}: сколько prev-окон полностью попадают
// в историю данных (first_review_date ≤ начало окна).
//   ≥ 2 → фронт показывает полный режим с average(prev1, prev2, [prev3])
//   < 2 → облегчённый режим (только current + подпись «недостаточно данных»)
//
// Prev3, Prev2, Prev1 — от старого к новому (prev3 раньше всех).
// Spark на фронте: [Prev3?, Prev2?, Prev1, Current] (если соотв. окно полное).
public sealed record RecentReviewsMetricResult(
    string Window,
    long CurrentCount,
    long Prev1Count,
    long Prev2Count,
    long Prev3Count,
    int FullPreviousWindows,
    DateTimeOffset CurrentFromInclusive,
    DateTimeOffset CurrentToExclusive);

internal sealed class RecentReviewsMetricService(ProcessingReadContext db)
    : IRecentReviewsMetricService
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int AllStarsCount = 5;

    public async Task<RecentReviewsMetricResult> ComputeAsync(
        RecentReviewsMetricQuery q, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // 5 границ окон от старого к новому: bound[0] начало prev3, bound[4]=now.
        // current = [bound[3], bound[4]], prev1 = [bound[2], bound[3]), и т.д.
        var bounds = ComputeBounds(now, q.Window);

        var branchIds = q.BranchIds.ToList();

        IQueryable<Review> baseQuery = db.AnalysisJobReviews
            .AsNoTracking()
            .Where(ajr => ajr.AnalysisJobId == q.JobId)
            .Join(db.Reviews, ajr => ajr.ReviewId, r => r.Id, (_, r) => r)
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

        // Один запрос на все 4 окна через CASE WHEN. PG считает за один проход
        // по индексу (branch_id, review_date).
        var prev3From = bounds[0];
        var prev2From = bounds[1];
        var prev1From = bounds[2];
        var currentFrom = bounds[3];
        var currentToExc = bounds[4];

        var counts = await baseQuery
            .Where(r => r.ReviewDate >= prev3From && r.ReviewDate < currentToExc)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Current = g.LongCount(r => r.ReviewDate >= currentFrom),
                Prev1 = g.LongCount(r => r.ReviewDate >= prev1From && r.ReviewDate < currentFrom),
                Prev2 = g.LongCount(r => r.ReviewDate >= prev2From && r.ReviewDate < prev1From),
                Prev3 = g.LongCount(r => r.ReviewDate >= prev3From && r.ReviewDate < prev2From),
            })
            .FirstOrDefaultAsync(ct);

        // Отдельный лёгкий запрос для определения «достаточности истории».
        // MIN(review_date) для филиала с теми же фильтрами sources/stars
        // (фильтр периода не применяем — окно у нас своё).
        var firstReviewDate = await baseQuery
            .Select(r => (DateTimeOffset?)r.ReviewDate)
            .MinAsync(ct);

        // Сколько prev-окон полностью покрыты историей. Окно «полное» если
        // first_review_date ≤ его начало (т.е. в этом окне могли быть отзывы).
        // Если первый отзыв позже середины prev3 — мы не знаем, было ли тогда
        // что-то, не считаем это окно валидным.
        int full = 0;
        if (firstReviewDate is { } first)
        {
            if (first <= prev1From) full = 1;
            if (first <= prev2From) full = 2;
            if (first <= prev3From) full = 3;
        }

        return new RecentReviewsMetricResult(
            Window: q.Window.ToString(),
            CurrentCount: counts?.Current ?? 0,
            Prev1Count: counts?.Prev1 ?? 0,
            Prev2Count: counts?.Prev2 ?? 0,
            Prev3Count: counts?.Prev3 ?? 0,
            FullPreviousWindows: full,
            CurrentFromInclusive: currentFrom,
            CurrentToExclusive: currentToExc);
    }

    // bounds[0..4]: prev3-start, prev2-start, prev1-start, current-start, current-end.
    // Для дневных окон — AddDays; для месяцев — AddMonths (реальные календарные).
    private static DateTimeOffset[] ComputeBounds(DateTimeOffset now, RecentReviewsWindow window)
    {
        // currentToExc = now (включая текущий момент). currentFrom = now - W.
        // Для совместимости со SQL <-comparison используем «exclusive верхней».
        var bounds = new DateTimeOffset[5];
        bounds[4] = now;
        bounds[3] = window switch
        {
            RecentReviewsWindow.Days7    => now.AddDays(-7),
            RecentReviewsWindow.Days30   => now.AddDays(-30),
            RecentReviewsWindow.Months3  => now.AddMonths(-3),
            RecentReviewsWindow.Months6  => now.AddMonths(-6),
            RecentReviewsWindow.Months12 => now.AddMonths(-12),
            _ => throw new ArgumentOutOfRangeException(nameof(window)),
        };
        bounds[2] = SubtractWindow(bounds[3], window);
        bounds[1] = SubtractWindow(bounds[2], window);
        bounds[0] = SubtractWindow(bounds[1], window);
        return bounds;
    }

    private static DateTimeOffset SubtractWindow(DateTimeOffset from, RecentReviewsWindow window) => window switch
    {
        RecentReviewsWindow.Days7    => from.AddDays(-7),
        RecentReviewsWindow.Days30   => from.AddDays(-30),
        RecentReviewsWindow.Months3  => from.AddMonths(-3),
        RecentReviewsWindow.Months6  => from.AddMonths(-6),
        RecentReviewsWindow.Months12 => from.AddMonths(-12),
        _ => throw new ArgumentOutOfRangeException(nameof(window)),
    };
}
