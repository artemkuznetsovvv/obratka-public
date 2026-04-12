using ParserService.Core.Models;

namespace ParserService.Sources.YandexMaps;

internal sealed class YandexReviewCollector
{
    private readonly YandexReviewApiClient _apiClient;
    private readonly YandexMapsOptions _options;
    private readonly ILogger _logger;

    public YandexReviewCollector(
        YandexReviewApiClient apiClient,
        YandexMapsOptions options,
        ILogger logger)
    {
        _apiClient = apiClient;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        YandexSession session,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var isIncremental = period.From != DateTimeOffset.MinValue;

        if (isIncremental)
        {
            _logger.LogInformation("[ApiCollector] Инкрементальный сбор для {BusinessId} с {From}",
                branch.ExternalId, period.From);
            return await CollectWithSortingAsync(session, branch, period, "by_time", stopOnDate: true, ct);
        }

        _logger.LogInformation("[ApiCollector] Полный сбор для {BusinessId} (двойная сортировка)", branch.ExternalId);

        _logger.LogDebug("[ApiCollector] Фаза 1: сортировка by_time...");
        var byTime = await CollectWithSortingAsync(session, branch, period, "by_time", stopOnDate: false, ct);

        _logger.LogDebug("[ApiCollector] Фаза 2: сортировка by_relevance_org...");
        var byRelevance = await CollectWithSortingAsync(session, branch, period, "by_relevance_org", stopOnDate: false, ct);

        var deduplicated = DeduplicateReviews(byTime, byRelevance);

        _logger.LogInformation(
            "[ApiCollector] Итого для {BusinessId}: {Total} уникальных ({ByTime} by_time + {ByRelevance} by_relevance, {Dupes} дубликатов убрано)",
            branch.ExternalId, deduplicated.Count, byTime.Count, byRelevance.Count,
            byTime.Count + byRelevance.Count - deduplicated.Count);

        return deduplicated;
    }

    private async Task<IReadOnlyList<RawReview>> CollectWithSortingAsync(
        YandexSession session,
        BranchTarget branch,
        DateRange period,
        string ranking,
        bool stopOnDate,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();

        _logger.LogDebug("[ApiCollector] Начинаю пагинацию ({Ranking}), макс. страниц: {MaxPages}, размер: {PageSize}",
            ranking, _options.MaxPages, _options.PageSize);

        for (int page = 1; page <= _options.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogDebug("[ApiCollector] Запрашиваю страницу {Page}/{MaxPages} ({Ranking})...",
                page, _options.MaxPages, ranking);

            var response = await _apiClient.FetchReviewsPageAsync(
                session, branch.ExternalId, page, _options.PageSize, ranking, ct);

            if (response.Reviews == null || response.Reviews.Count == 0)
            {
                _logger.LogDebug("[ApiCollector] Страница {Page} пустая — завершаю пагинацию ({Ranking})", page, ranking);
                break;
            }

            _logger.LogDebug("[ApiCollector] Страница {Page}: получено {Count} отзывов, hasMore={HasMore}",
                page, response.Reviews.Count, response.HasMore);

            bool reachedDateBound = false;
            int skippedNoId = 0, skippedDate = 0, added = 0;

            foreach (var dto in response.Reviews)
            {
                if (dto.ReviewId == null || dto.Rating == null || string.IsNullOrEmpty(dto.UpdatedTime))
                {
                    skippedNoId++;
                    continue;
                }

                if (!DateTimeOffset.TryParse(dto.UpdatedTime, out var date))
                {
                    _logger.LogDebug("[ApiCollector] Не удалось распарсить дату: '{Date}' (reviewId={Id})",
                        dto.UpdatedTime, dto.ReviewId);
                    continue;
                }

                if (date < period.From)
                {
                    skippedDate++;
                    if (stopOnDate)
                    {
                        reachedDateBound = true;
                        _logger.LogDebug("[ApiCollector] Достигнута дата-граница: отзыв {Date} < {From}",
                            date, period.From);
                        break;
                    }
                    continue;
                }

                if (date > period.To)
                {
                    skippedDate++;
                    continue;
                }

                added++;
                reviews.Add(new RawReview(
                    ExternalId: dto.ReviewId,
                    Text: dto.Text ?? "",
                    Date: date,
                    Stars: dto.Rating.Value,
                    BranchId: branch.BranchId,
                    AuthorName: dto.Author?.Name,
                    AuthorPublicId: dto.Author?.PublicId,
                    TextLanguage: dto.TextLanguage));
            }

            _logger.LogDebug("[ApiCollector] Страница {Page} ({Ranking}): +{Added} добавлено, {SkipDate} вне периода, {SkipId} без ID. Всего: {Total}",
                page, ranking, added, skippedDate, skippedNoId, reviews.Count);

            if (reachedDateBound)
            {
                _logger.LogInformation("[ApiCollector] Дата-граница достигнута на стр. {Page} ({Ranking}) — останавливаюсь", page, ranking);
                break;
            }

            if (response.HasMore != true)
            {
                _logger.LogDebug("[ApiCollector] hasMore=false на стр. {Page} ({Ranking}) — больше страниц нет", page, ranking);
                break;
            }

            // Delay between pages
            var delay = Random.Shared.Next(_options.DelayBetweenPagesMinMs, _options.DelayBetweenPagesMaxMs + 1);
            _logger.LogDebug("[ApiCollector] Пауза {Delay}мс перед следующей страницей", delay);
            await Task.Delay(delay, ct);
        }

        _logger.LogInformation("[ApiCollector] Сортировка {Ranking}: собрано {Count} отзывов", ranking, reviews.Count);
        return reviews;
    }

    private static List<RawReview> DeduplicateReviews(
        IReadOnlyList<RawReview> first,
        IReadOnlyList<RawReview> second)
    {
        var seen = new HashSet<string>();
        var result = new List<RawReview>();

        foreach (var review in first.Concat(second))
        {
            if (seen.Add(review.ExternalId))
                result.Add(review);
        }

        return result;
    }
}
