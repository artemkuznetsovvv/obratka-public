using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using ParserService.Core.Models;

namespace ParserService.Sources.YandexMaps;

/// <summary>
/// Collects reviews by scrolling the Yandex Maps reviews page in a real browser
/// and intercepting fetchReviews API responses. The browser's own JS handles
/// csrf tokens, session IDs, hashes, etc.
/// </summary>
internal sealed class BrowserScrollCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly YandexMapsOptions _options;
    private readonly ILogger _logger;

    public BrowserScrollCollector(YandexMapsOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        IBrowserContext browserContext,
        string orgUrl,
        BranchTarget branch,
        DateRange period,
        bool headful,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();
        var seenIds = new HashSet<string>();
        var interceptedResponses = new ConcurrentQueue<YandexReviewsResponse>();
        var hasMore = true;

        var page = await browserContext.NewPageAsync();
        try
        {
            _logger.LogInformation("[BrowserScroll] Начинаю сбор отзывов для {BusinessId} (URL: {Url})",
                branch.ExternalId, orgUrl);

            // --- Step 1: Set up response interception ---
            page.Response += (_, response) =>
            {
                if (!response.Url.Contains("/maps/api/business/fetchReviews"))
                    return;

                if (response.Status != 200)
                {
                    _logger.LogWarning("[BrowserScroll] fetchReviews вернул статус {Status}, URL: {Url}",
                        response.Status, response.Url);
                    return;
                }

                _ = CaptureResponseAsync(response, interceptedResponses);
            };
            _logger.LogDebug("[BrowserScroll] Перехват fetchReviews ответов настроен");

            // --- Step 2: Optional warm-up ---
            if (_options.WarmUpSession)
            {
                _logger.LogDebug("[BrowserScroll] Прогрев сессии включён, открываю yandex.ru...");
                await YandexSession.WarmUpAsync(page, _logger, ct);
            }

            // --- Step 3: Navigate to reviews page ---
            var reviewsUrl = BuildReviewsUrl(orgUrl);
            _logger.LogInformation("[BrowserScroll] Перехожу на страницу отзывов: {Url}", reviewsUrl);

            await page.GotoAsync(reviewsUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });

            _logger.LogDebug("[BrowserScroll] Страница загружена, текущий URL: {Url}", page.Url);
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Step 4: Handle captcha ---
            var html = await page.ContentAsync();
            _logger.LogDebug("[BrowserScroll] Проверяю наличие капчи...");
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                _logger.LogWarning("[BrowserScroll] SmartCaptcha обнаружена на странице отзывов!");
                var captchaHandler = new SmartCaptchaHandler(_logger, _options);
                var solved = await captchaHandler.TrySolveCaptchaAsync(page, headful, ct);

                if (!solved)
                    throw new SmartCaptchaException(
                        "SmartCaptcha could not be solved. Try residential/mobile proxies or reduce request rate.");

                html = await page.ContentAsync();
                if (SmartCaptchaHandler.IsCaptchaPage(html))
                    throw new SmartCaptchaException(
                        "SmartCaptcha appeared again after solving. IP is likely flagged.");

                _logger.LogInformation("[BrowserScroll] Капча решена, продолжаю сбор");
                await Task.Delay(Random.Shared.Next(2000, 4000), ct);
            }
            else
            {
                _logger.LogDebug("[BrowserScroll] Капча не обнаружена — всё чисто");
            }

            // --- Step 5: Select sort by date ---
            _logger.LogDebug("[BrowserScroll] Переключаю сортировку на 'По новизне'...");
            await SelectSortByDateAsync(page, ct);

            // --- Step 6: Process initial intercepted responses ---
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
            var reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);

            _logger.LogInformation("[BrowserScroll] Первая порция: {Count} отзывов, есть ещё: {HasMore}, дата-граница: {DateBound}",
                reviews.Count, hasMore, reachedDateBound);

            // --- Step 7: Scroll loop ---
            _logger.LogDebug("[BrowserScroll] Начинаю скролл-цикл (макс. {Max} попыток)", _options.MaxScrollAttempts);
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts && hasMore && !reachedDateBound; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                await TriggerNextPageAsync(page, ct);

                // Wait for intercepted response to arrive (poll queue with timeout)
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(_options.DelayBetweenPagesMaxMs * 2);
                var waitStart = DateTime.UtcNow;
                while (interceptedResponses.IsEmpty && DateTime.UtcNow < waitDeadline)
                {
                    await Task.Delay(300, ct);
                }

                var waitedMs = (int)(DateTime.UtcNow - waitStart).TotalMilliseconds;
                if (interceptedResponses.IsEmpty)
                    _logger.LogDebug("[BrowserScroll] Скролл #{Attempt}: ответ не получен за {WaitedMs}мс", attempt + 1, waitedMs);
                else
                    _logger.LogDebug("[BrowserScroll] Скролл #{Attempt}: ответ получен за {WaitedMs}мс", attempt + 1, waitedMs);

                // Human-like pause after response arrives
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);

                reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);

                var newReviews = reviews.Count - beforeCount;

                if (newReviews == 0)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug("[BrowserScroll] Скролл #{Attempt}: нет новых отзывов (пустых подряд: {Streak}/{Max})",
                        attempt + 1, consecutiveEmpty, maxConsecutiveEmpty);

                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogInformation("[BrowserScroll] {Max} пустых скроллов подряд — останавливаюсь", maxConsecutiveEmpty);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug("[BrowserScroll] Скролл #{Attempt}: +{New} новых, всего {Total} отзывов (уникальных ID: {Unique})",
                        attempt + 1, newReviews, reviews.Count, seenIds.Count);
                }
            }

            _logger.LogInformation(
                "[BrowserScroll] Сбор завершён для {BusinessId}: {Count} отзывов, уникальных ID: {Unique}, hasMore: {HasMore}, дата-граница: {DateBound}",
                branch.ExternalId, reviews.Count, seenIds.Count, hasMore, reachedDateBound);

            return reviews;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task CaptureResponseAsync(
        IResponse response,
        ConcurrentQueue<YandexReviewsResponse> queue)
    {
        try
        {
            var body = await response.TextAsync();
            _logger.LogDebug("[BrowserScroll] Перехвачен fetchReviews ответ ({Length} байт)", body.Length);

            var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(body, JsonOptions);
            var parsed = root?.Data;
            if (parsed?.Reviews != null)
            {
                queue.Enqueue(parsed);
                _logger.LogDebug("[BrowserScroll] Распарсен ответ: {Count} отзывов, hasMore={HasMore}",
                    parsed.Reviews.Count, parsed.HasMore);
            }
            else
            {
                _logger.LogWarning("[BrowserScroll] Ответ fetchReviews без отзывов. Тело (первые 500 симв.): {Body}",
                    body.Length > 500 ? body[..500] : body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BrowserScroll] Ошибка парсинга fetchReviews ответа");
        }
    }

    private async Task SelectSortByDateAsync(IPage page, CancellationToken ct)
    {
        // The sort control is a div.rating-ranking-view with role="button" (NOT a <button>).
        // It contains <span>По умолчанию</span>. Clicking it opens a <dialog> with options.

        // Step 1: Wait for the sort control to appear and click it.
        var dropdownSelectors = new[]
        {
            ".rating-ranking-view",
            ".business-reviews-card-view__ranking [role='button']",
            "[class*='ranking-view']",
        };

        bool dropdownOpened = false;
        foreach (var selector in dropdownSelectors)
        {
            try
            {
                var el = await page.WaitForSelectorAsync(selector,
                    new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                if (el != null)
                {
                    var text = await el.TextContentAsync() ?? "";
                    if (text.Contains("По новизне"))
                    {
                        _logger.LogDebug("Sort already set to 'По новизне', skipping");
                        return;
                    }

                    await el.ClickAsync();
                    _logger.LogDebug("Opened sort dropdown via: {Selector}", selector);
                    dropdownOpened = true;
                    break;
                }
            }
            catch
            {
                // Try next selector
            }
        }

        if (!dropdownOpened)
        {
            _logger.LogWarning("Could not open sort dropdown, using default sort");
            return;
        }

        await Task.Delay(Random.Shared.Next(500, 1000), ct);

        // Step 2: Click "По новизне" inside the opened popup.
        // Options are <div role="button" class="rating-ranking-view__popup-line">, NOT <button>.
        // Container is [role="dialog"], NOT <dialog>.
        var optionSelectors = new[]
        {
            ".rating-ranking-view__popup-line[aria-label='По новизне']",
            "[role='dialog'] [role='button']:has-text('По новизне')",
            "[role='dialog'] :has-text('По новизне')",
        };

        foreach (var selector in optionSelectors)
        {
            try
            {
                var el = await page.WaitForSelectorAsync(selector,
                    new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                if (el != null)
                {
                    await el.ClickAsync();
                    _logger.LogDebug("Selected 'По новизне' via: {Selector}", selector);
                    await Task.Delay(Random.Shared.Next(2000, 3000), ct);
                    return;
                }
            }
            catch
            {
                // Try next selector
            }
        }

        _logger.LogWarning("Could not find 'По новизне' option in sort dialog");
    }

    private async Task TriggerNextPageAsync(IPage page, CancellationToken ct)
    {
        // Yandex Maps uses infinite scroll inside a sidebar panel (div.scroll__container),
        // NOT window scroll. body has overflow:hidden. Scrolling window does nothing.
        // Smooth scroll in 3-6 steps to simulate human behavior.
        await page.EvaluateAsync(@"async () => {
            const c = document.querySelector('.scroll__container');
            if (!c) { window.scrollTo(0, document.body.scrollHeight); return; }

            const target = c.scrollHeight;
            const current = c.scrollTop;
            const distance = target - current;
            if (distance <= 0) return;

            const steps = 3 + Math.floor(Math.random() * 4); // 3-6 steps
            for (let i = 1; i <= steps; i++) {
                // Ease-out: bigger jumps at start, smaller at end
                const progress = 1 - Math.pow(1 - i / steps, 2);
                c.scrollTop = current + distance * progress;
                await new Promise(r => setTimeout(r, 80 + Math.random() * 120));
            }
        }");
        _logger.LogDebug("[BrowserScroll] Плавный скролл sidebar (ease-out)");
    }

    private bool DrainQueue(
        ConcurrentQueue<YandexReviewsResponse> queue,
        List<RawReview> reviews,
        HashSet<string> seenIds,
        BranchTarget branch,
        DateRange period,
        ref bool hasMore)
    {
        bool reachedDateBound = false;

        while (queue.TryDequeue(out var response))
        {
            if (response.HasMore == false)
                hasMore = false;

            if (response.Reviews == null)
                continue;

            foreach (var dto in response.Reviews)
            {
                if (dto.ReviewId == null || dto.Rating == null || string.IsNullOrEmpty(dto.UpdatedTime))
                    continue;

                if (!seenIds.Add(dto.ReviewId))
                    continue;

                if (!DateTimeOffset.TryParse(dto.UpdatedTime, out var date))
                    continue;

                if (date < period.From)
                {
                    reachedDateBound = true;
                    continue;
                }

                if (date > period.To)
                    continue;

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
        }

        return reachedDateBound;
    }

    private static string BuildReviewsUrl(string orgUrl)
    {
        orgUrl = orgUrl.TrimEnd('/');
        if (!orgUrl.EndsWith("/reviews", StringComparison.OrdinalIgnoreCase))
            orgUrl += "/reviews/";
        else if (!orgUrl.EndsWith('/'))
            orgUrl += "/";
        return orgUrl;
    }
}
