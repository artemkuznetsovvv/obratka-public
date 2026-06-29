namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 7 «Новые отзывы за период».
// Counts для current + 3 previous окон + fullPreviousWindows ∈ {0..3}.
// Фронт решает режим:
//   fullPreviousWindows < 2 → облегчённый (только currentCount + «недостаточно данных»)
//   ≥ 2 → полный режим: «обычное» = average(prev1, prev2[, prev3])
// prev3/prev2/prev1 — от старого к новому.
public sealed record RecentReviewsMetricDto(
    string Window,
    long CurrentCount,
    long Prev1Count,
    long Prev2Count,
    long Prev3Count,
    int FullPreviousWindows,
    DateTimeOffset CurrentFromInclusive,
    DateTimeOffset CurrentToExclusive);
