using ParserService.Core.Models;

namespace ParserService.Sources.YandexMaps;

/// <summary>
/// Результат одного прохода сбора отзывов. HasMore/ReachedDateBound нужны плагину, чтобы
/// отличить "Яндекс отдал всё, что есть" (ретраить бесполезно) от "сбор оборвался
/// подозрительно рано — вероятно, тихая фильтрация по IP" (стоит попробовать другой прокси).
/// </summary>
internal sealed record YandexCollectionOutcome(
    IReadOnlyList<RawReview> Reviews,
    bool HasMore,
    bool ReachedDateBound);
