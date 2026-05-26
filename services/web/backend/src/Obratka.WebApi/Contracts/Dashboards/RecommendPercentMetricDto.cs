namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 6 «Сколько клиентов рекомендуют». Counts → фронт считает % сам.
// hasPreviousPeriod=false когда фильтр периода не задан (или period.from/to
// одна из границ null) — UI показывает тренд как «—».
//
// current.totalNonEmpty=0 → empty state «Нет данных», % и тренд скрыты.
public sealed record RecommendPercentMetricDto(
    RecommendPercentWindowDto Current,
    RecommendPercentWindowDto Previous,
    bool HasPreviousPeriod);

public sealed record RecommendPercentWindowDto(
    long Positive,
    long TotalNonEmpty);
