using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using ParserService.Core.Models;

namespace ParserService.Sources.TwoGis;

/// <summary>
/// Вариант A: сбор отзывов через Playwright.
/// Открывает страницу отзывов в браузере, скроллит sidebar
/// и перехватывает ответы от public-api.reviews.2gis.com.
/// </summary>
internal sealed class TwoGisBrowserCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TwoGisOptions _options;
    private readonly ILogger _logger;

    public TwoGisBrowserCollector(TwoGisOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        IBrowserContext browserContext,
        string firmId,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();
        var seenIds = new HashSet<string>();
        var interceptedResponses = new ConcurrentQueue<TwoGisReviewsResponse>();
        var hasMore = true;

        var page = await browserContext.NewPageAsync();
        try
        {
            _logger.LogInformation("[2GIS-Browser] Начинаю сбор для firmId={FirmId}", firmId);

            // --- Перехват ответов API до навигации ---
            page.Response += (_, response) =>
            {
                if (!response.Url.Contains("/branches/") || !response.Url.Contains("/reviews"))
                    return;
                if (!response.Url.Contains("public-api.reviews.2gis.com"))
                    return;
                if (response.Status != 200)
                {
                    _logger.LogWarning("[2GIS-Browser] API вернул статус {Status}: {Url}",
                        response.Status, response.Url);
                    return;
                }

                _ = CaptureResponseAsync(response, interceptedResponses);
            };
            _logger.LogDebug("[2GIS-Browser] Перехват API ответов настроен");

            // --- Навигация на страницу отзывов ---
            var url = $"https://2gis.ru/{_options.DefaultCity}/firm/{firmId}/tab/reviews";
            _logger.LogInformation("[2GIS-Browser] Открываю {Url}", url);

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = _options.NavigationTimeoutMs
            });
            _logger.LogDebug("[2GIS-Browser] Страница загружена: {Url}", page.Url);

            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Переключаем сортировку на "По дате" ---
            _logger.LogDebug("[2GIS-Browser] Переключаю сортировку на 'По дате'...");
            await SelectSortByDateAsync(page, ct);
            await Task.Delay(Random.Shared.Next(2000, 3000), ct);

            // --- Обработка первой порции ---
            var reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);
            _logger.LogInformation("[2GIS-Browser] Первая порция: {Count} отзывов, hasMore={HasMore}",
                reviews.Count, hasMore);

            // --- Скролл-цикл ---
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts && hasMore && !reachedDateBound; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                await ScrollDownAsync(page, ct);

                // Ждём перехваченный ответ
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(_options.DelayBetweenPagesMaxMs * 2);
                while (interceptedResponses.IsEmpty && DateTime.UtcNow < waitDeadline)
                    await Task.Delay(300, ct);

                // Пауза между скроллами
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);

                reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);

                var newReviews = reviews.Count - beforeCount;
                if (newReviews == 0)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug("[2GIS-Browser] Скролл #{N}: нет новых (пустых: {Streak}/{Max})",
                        attempt + 1, consecutiveEmpty, maxConsecutiveEmpty);
                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogInformation("[2GIS-Browser] {Max} пустых скроллов — останавливаюсь",
                            maxConsecutiveEmpty);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug("[2GIS-Browser] Скролл #{N}: +{New}, всего {Total}",
                        attempt + 1, newReviews, reviews.Count);
                }
            }

            _logger.LogInformation(
                "[2GIS-Browser] Сбор завершён: firmId={FirmId}, {Count} отзывов",
                firmId, reviews.Count);

            return reviews;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task CaptureResponseAsync(
        IResponse response,
        ConcurrentQueue<TwoGisReviewsResponse> queue)
    {
        try
        {
            var body = await response.TextAsync();
            _logger.LogDebug("[2GIS-Browser] Перехвачен ответ API ({Length} байт)", body.Length);

            var parsed = JsonSerializer.Deserialize<TwoGisReviewsResponse>(body, JsonOptions);
            if (parsed?.Reviews != null)
            {
                queue.Enqueue(parsed);
                _logger.LogDebug("[2GIS-Browser] Распарсено: {Count} отзывов, nextLink={HasNext}",
                    parsed.Reviews.Count, parsed.Meta?.NextLink != null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[2GIS-Browser] Ошибка парсинга API ответа");
        }
    }

    private async Task SelectSortByDateAsync(IPage page, CancellationToken ct)
    {
        // 2GIS использует динамические CSS-классы.
        // Ищем dropdown сортировки по ARIA-атрибутам и data-атрибутам.
        var sortClicked = await page.EvaluateAsync<bool>("""
            (() => {
                // Ищем все элементы, которые содержат текст о сортировке
                const allEls = document.querySelectorAll('*');
                for (const el of allEls) {
                    const text = (el.textContent || '').trim();
                    // Dropdown обычно содержит "По умолчанию" или "Сначала полезные"
                    if ((text === 'По умолчанию' || text === 'Сначала полезные' || text === 'Сначала новые')
                        && el.children.length === 0
                        && el.offsetParent !== null) {
                        // Кликаем на родительский элемент (контейнер dropdown)
                        const parent = el.closest('[role="button"]') || el.closest('button') || el.parentElement;
                        if (parent) {
                            parent.click();
                            return true;
                        }
                    }
                }
                return false;
            })()
        """);

        if (!sortClicked)
        {
            _logger.LogWarning("[2GIS-Browser] Не удалось найти dropdown сортировки");
            return;
        }

        _logger.LogDebug("[2GIS-Browser] Dropdown сортировки открыт");
        await Task.Delay(Random.Shared.Next(500, 1000), ct);

        // Выбираем "Сначала новые"
        var optionClicked = await page.EvaluateAsync<bool>("""
            (() => {
                const allEls = document.querySelectorAll('*');
                for (const el of allEls) {
                    const text = (el.textContent || '').trim();
                    if (text === 'Сначала новые' && el.children.length === 0 && el.offsetParent !== null) {
                        const clickable = el.closest('[role="button"]') || el.closest('[role="option"]')
                            || el.closest('button') || el.closest('li') || el;
                        clickable.click();
                        return true;
                    }
                }
                return false;
            })()
        """);

        if (optionClicked)
            _logger.LogDebug("[2GIS-Browser] Сортировка 'Сначала новые' выбрана");
        else
            _logger.LogWarning("[2GIS-Browser] Не удалось выбрать 'Сначала новые'");
    }

    private async Task ScrollDownAsync(IPage page, CancellationToken ct)
    {
        // 2GIS sidebar имеет data-scroll="true" или .scroll__container
        await page.EvaluateAsync("""
            async () => {
                const c = document.querySelector('[data-scroll="true"]')
                    || document.querySelector('.scroll__container')
                    || document.scrollingElement;
                if (!c) return;

                const target = c.scrollHeight;
                const current = c.scrollTop;
                const distance = target - current;
                if (distance <= 0) return;

                // Плавный скролл (ease-out)
                const steps = 3 + Math.floor(Math.random() * 4);
                for (let i = 1; i <= steps; i++) {
                    const progress = 1 - Math.pow(1 - i / steps, 2);
                    c.scrollTop = current + distance * progress;
                    await new Promise(r => setTimeout(r, 80 + Math.random() * 120));
                }
            }
        """);
        _logger.LogDebug("[2GIS-Browser] Скролл sidebar");
    }

    private bool DrainQueue(
        ConcurrentQueue<TwoGisReviewsResponse> queue,
        List<RawReview> reviews,
        HashSet<string> seenIds,
        BranchTarget branch,
        DateRange period,
        ref bool hasMore)
    {
        bool reachedDateBound = false;

        while (queue.TryDequeue(out var response))
        {
            if (response.Meta?.NextLink == null)
                hasMore = false;

            if (response.Reviews == null)
                continue;

            foreach (var dto in response.Reviews)
            {
                if (dto.Id == null || dto.Rating == null)
                    continue;

                if (!seenIds.Add(dto.Id))
                    continue;

                if (!DateTimeOffset.TryParse(dto.DateCreated, out var date))
                    continue;

                if (date < period.From)
                {
                    reachedDateBound = true;
                    continue;
                }

                if (date > period.To)
                    continue;

                reviews.Add(new RawReview(
                    ExternalId: dto.Id,
                    Text: dto.Text ?? "",
                    Date: date,
                    Stars: dto.Rating.Value,
                    BranchId: branch.BranchId,
                    AuthorName: dto.User?.Name,
                    AuthorPublicId: dto.User?.PublicId,
                    TextLanguage: null));
            }
        }

        return reachedDateBound;
    }
}
