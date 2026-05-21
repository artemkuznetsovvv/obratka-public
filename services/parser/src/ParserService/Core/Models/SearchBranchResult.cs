namespace ParserService.Core.Models;

/// <summary>
/// Результат поиска одной организации/филиала в источнике.
/// </summary>
/// <param name="ReviewCount">
/// Число, которое источник показывает рядом с рейтингом на странице/в карточке поиска.
/// ВАЖНО: для 2GIS и Яндекса это «число оценок» (rating votes), НЕ «число отзывов с
/// текстом». Поле сохранено как ReviewCount по историческим причинам — клиенты привязаны
/// к нему как к «индикатору популярности». Настоящее число отзывов отдаётся отдельным
/// полем <see cref="RealReviewsCount"/> когда удалось его достать.
/// Для Google карточка поиска и так показывает реальное число отзывов, поэтому
/// ReviewCount == RealReviewsCount.
/// </param>
/// <param name="RealReviewsCount">
/// Число отзывов с текстом, когда удалось получить точное значение из источника:
/// • 2GIS — из API <c>catalog.api.2gis.ru/3.0/markers/clustered</c> поле
///   <c>reviews.general_review_count</c>;
/// • Яндекс (одиночный редирект на org-page) — из бейджа таба
///   <c>.tabs-select-view__title._name_reviews</c> («Отзывы&lt;N&gt;»);
/// • Google — то же что ReviewCount.
/// null когда источник реальное число не отдаёт (Яндекс multi-result list).
/// </param>
public record SearchBranchResult(
    SourceType Source,
    string ExternalId,
    string ExternalUrl,
    string Name,
    string Address,
    double? Rating,
    int? ReviewCount,
    int? RealReviewsCount = null
);
