namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 3 «Настроение клиентов». Counts по 3 уровням тональности
// (позитивный/нейтральный/негативный). Пустые overall_sentiment исключены
// из подсчёта (по спеке М3 «Исключение из расчёта»).
//
// totalNonEmpty=0 → карточка показывает «Нет данных», фраза-вывод и полоса
// скрыты.
public sealed record SentimentDistributionMetricDto(
    long Positive,
    long Neutral,
    long Negative,
    long TotalNonEmpty);
