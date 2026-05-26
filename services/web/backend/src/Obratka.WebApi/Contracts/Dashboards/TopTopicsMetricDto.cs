namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 5 «О чём говорят чаще всего» — топ-3 темы.
// totalReviewsInPeriod нужен фронту для расчёта доли темы (reviewCount / total).
// Если topics пустой и totalReviewsInPeriod=0 — карточка показывает empty state.
public sealed record TopTopicsMetricDto(
    IReadOnlyList<TopicAggregateDto> Topics,
    long TotalReviewsInPeriod);

public sealed record TopicAggregateDto(
    string Topic,
    long ReviewCount,
    long PositiveMentions,
    long NegativeMentions);
