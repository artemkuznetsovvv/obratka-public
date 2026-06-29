namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 2: «Средний рейтинг».
// totalAverage / averageRating per source — null когда нет ни одного отзыва
// со stars в соответствующем срезе. count нужен фронту для:
//   а) скрытия подписи если 0 отзывов вообще,
//   б) пересчёта total weighted-average по выбранным в фильтре source
//      (симметрично М1 с total).
public sealed record AverageRatingMetricDto(
    double? TotalAverage,
    int TotalCount,
    IReadOnlyList<AverageRatingSourceDto> BySource);

public sealed record AverageRatingSourceDto(
    string Source,
    double? Average,
    int Count);
