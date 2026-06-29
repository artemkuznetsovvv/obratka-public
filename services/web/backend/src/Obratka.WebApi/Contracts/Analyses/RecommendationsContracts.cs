namespace Obratka.WebApi.Contracts.Analyses;

// Список рекомендаций по результатам анализа. Сортировка sort_order ASC.
// Empty list — нормальный ответ когда LLM не выдал ни одной рекомендации.
public sealed record RecommendationListDto(IReadOnlyList<RecommendationDto> Items);

// priority: 1..3 (1 = высокий, 3 = низкий) — определяется LLM.
// evidence — массив строк-цитат из отзывов, без id/даты (LLM не привязывает).
public sealed record RecommendationDto(
    long Id,
    short Priority,
    string Topic,
    string Title,
    string Body,
    string? ExpectedImpact,
    IReadOnlyList<string> Evidence);
