using ParserService.Core.Models;

namespace ParserService.Sources.TwoGis;

/// <summary>
/// Вариант B: сбор отзывов через прямые HTTP-запросы к public-api.reviews.2gis.com.
/// Не требует браузера для пагинации — только для извлечения API-ключа (если не задан в конфиге).
/// </summary>
internal sealed class TwoGisApiCollector
{
    private readonly TwoGisApiClient _apiClient;
    private readonly TwoGisOptions _options;
    private readonly ILogger _logger;

    public TwoGisApiCollector(TwoGisApiClient apiClient, TwoGisOptions options, ILogger logger)
    {
        _apiClient = apiClient;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        string firmId,
        string apiKey,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();
        var seenIds = new HashSet<string>();
        var offset = 0;
        var reachedDateBound = false;

        _logger.LogInformation(
            "[2GIS-ApiCollector] Начинаю сбор: firmId={FirmId}, период: {From} — {To}",
            firmId, period.From, period.To);

        // Сортировка по дате — новые первыми, для поддержки date_from
        const string sortBy = "date_created";

        while (!reachedDateBound)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _apiClient.FetchReviewsPageAsync(
                firmId, apiKey, _options.PageSize, offset, sortBy, ct);

            if (response.Reviews == null || response.Reviews.Count == 0)
            {
                _logger.LogDebug("[2GIS-ApiCollector] Пустая страница на offset={Offset}, завершаю", offset);
                break;
            }

            var addedOnPage = 0;
            var skippedOnPage = 0;

            foreach (var dto in response.Reviews)
            {
                if (dto.Id == null || dto.Rating == null)
                    continue;

                if (!seenIds.Add(dto.Id))
                {
                    skippedOnPage++;
                    continue;
                }

                if (!DateTimeOffset.TryParse(dto.DateCreated, out var date))
                    continue;

                // При инкрементальном сборе — останавливаемся когда дошли до старых отзывов
                if (date < period.From)
                {
                    reachedDateBound = true;
                    _logger.LogInformation(
                        "[2GIS-ApiCollector] Достигнута граница даты: отзыв {Id} от {Date} < {From}",
                        dto.Id, date, period.From);
                    break;
                }

                if (date > period.To)
                {
                    skippedOnPage++;
                    continue;
                }

                reviews.Add(MapToRawReview(dto, date, branch));
                addedOnPage++;
            }

            _logger.LogDebug(
                "[2GIS-ApiCollector] Offset={Offset}: +{Added} новых, {Skipped} пропущено, всего {Total}",
                offset, addedOnPage, skippedOnPage, reviews.Count);

            // Нет следующей страницы — конец
            if (response.Meta?.NextLink == null)
            {
                _logger.LogDebug("[2GIS-ApiCollector] NextLink отсутствует — все страницы обработаны");
                break;
            }

            offset += _options.PageSize;

            // Пауза между страницами
            var delay = Random.Shared.Next(_options.DelayBetweenPagesMinMs, _options.DelayBetweenPagesMaxMs + 1);
            _logger.LogDebug("[2GIS-ApiCollector] Пауза {Delay}мс перед следующей страницей", delay);
            await Task.Delay(delay, ct);
        }

        _logger.LogInformation(
            "[2GIS-ApiCollector] Сбор завершён: firmId={FirmId}, {Count} отзывов, уникальных ID: {Unique}",
            firmId, reviews.Count, seenIds.Count);

        return reviews;
    }

    private static RawReview MapToRawReview(TwoGisReviewDto dto, DateTimeOffset date, BranchTarget branch)
    {
        return new RawReview(
            ExternalId: dto.Id!,
            Text: dto.Text ?? "",
            Date: date,
            Stars: dto.Rating!.Value,
            BranchId: branch.BranchId,
            AuthorName: dto.User?.Name,
            AuthorPublicId: dto.User?.PublicId,
            TextLanguage: null);
    }
}
