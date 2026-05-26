namespace Obratka.WebApi.Contracts.Dashboards;

// Список отзывов конкретной тональности для модалки раскрытия (М3/О3).
// Сортировка: ReviewDate DESC, тiebreak по Id DESC. hasMore=true → клиент
// может запросить следующую страницу с offset += items.Count.
public sealed record SentimentReviewsDto(
    IReadOnlyList<SentimentReviewItemDto> Items,
    bool HasMore);

public sealed record SentimentReviewItemDto(
    long Id,
    string Source,
    DateTimeOffset ReviewDate,
    short? Stars,
    string Text);
