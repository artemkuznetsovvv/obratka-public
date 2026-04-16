using System.Collections.Concurrent;
using Microsoft.Playwright;
using ParserService.Core.Models;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Collects reviews by scrolling Google Maps reviews tab in browser
/// and intercepting listugcposts API responses.
/// Best of both worlds: looks like real user + full API data quality
/// (exact timestamps, original text, authorId, language).
/// </summary>
internal sealed class GoogleMapsHybridCollector
{
    private readonly GoogleMapsOptions _options;
    private readonly ILogger _logger;

    public GoogleMapsHybridCollector(GoogleMapsOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        IBrowserContext browserContext,
        string placeUrl,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();
        var seenIds = new HashSet<string>();
        var interceptedBatch = new ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>>();

        var page = await browserContext.NewPageAsync();
        try
        {
            _logger.LogInformation("[GMaps-Hybrid] Начинаю сбор для {ExternalId} (URL: {Url})",
                branch.ExternalId, placeUrl);

            // --- Set up API response interception ---
            page.Response += (_, response) =>
            {
                if (!response.Url.Contains("/maps/rpc/listugcposts")) return;
                if (response.Status != 200) return;

                _ = CaptureResponseAsync(response, interceptedBatch);
            };
            _logger.LogDebug("[GMaps-Hybrid] Перехват listugcposts настроен");

            // --- Navigate to place page ---
            await page.GotoAsync(placeUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });
            await GoogleMapsConsentHelper.DismissConsentIfNeededAsync(page, _logger, ct);
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Click reviews tab ---
            await ClickReviewsTabAsync(page, ct);

            // --- Select sort by newest ---
            await SelectSortByNewestAsync(page, ct);

            // --- Process initial intercepted responses ---
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
            var reachedDateBound = DrainQueue(interceptedBatch, reviews, seenIds, branch, period);

            _logger.LogInformation("[GMaps-Hybrid] Первая порция: {Count} отзывов, дата-граница: {DateBound}",
                reviews.Count, reachedDateBound);

            // --- Scroll loop ---
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts && !reachedDateBound; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                // Scroll down to trigger next page load
                await ScrollReviewsPanelAsync(page);

                // Wait for intercepted response
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(_options.DelayBetweenPagesMaxMs * 2);
                while (interceptedBatch.IsEmpty && DateTime.UtcNow < waitDeadline)
                    await Task.Delay(300, ct);

                // Human-like pause
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);

                reachedDateBound = DrainQueue(interceptedBatch, reviews, seenIds, branch, period);

                var newReviews = reviews.Count - beforeCount;
                if (newReviews == 0)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug("[GMaps-Hybrid] Скролл #{N}: нет новых (пустых: {Streak}/{Max})",
                        attempt + 1, consecutiveEmpty, maxConsecutiveEmpty);

                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogInformation("[GMaps-Hybrid] {Max} пустых скроллов — останавливаюсь",
                            maxConsecutiveEmpty);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug("[GMaps-Hybrid] Скролл #{N}: +{New}, всего {Total}",
                        attempt + 1, newReviews, reviews.Count);
                }
            }

            _logger.LogInformation(
                "[GMaps-Hybrid] Сбор завершён: {ExternalId}, {Count} отзывов, уникальных: {Unique}",
                branch.ExternalId, reviews.Count, seenIds.Count);

            return reviews;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task CaptureResponseAsync(
        IResponse response,
        ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>> queue)
    {
        try
        {
            var body = await response.TextAsync();
            _logger.LogDebug("[GMaps-Hybrid] Перехвачен listugcposts ответ ({Length} байт)", body.Length);

            var root = GoogleMapsResponseParser.Parse(body);
            var reviews = GoogleMapsResponseParser.GetReviews(root);

            if (reviews.Count > 0)
            {
                queue.Enqueue(reviews);
                _logger.LogDebug("[GMaps-Hybrid] Распарсено: {Count} отзывов", reviews.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GMaps-Hybrid] Ошибка парсинга listugcposts ответа");
        }
    }

    private bool DrainQueue(
        ConcurrentQueue<IReadOnlyList<GoogleMapsReviewDto>> queue,
        List<RawReview> reviews,
        HashSet<string> seenIds,
        BranchTarget branch,
        DateRange period)
    {
        bool reachedDateBound = false;

        while (queue.TryDequeue(out var batch))
        {
            foreach (var dto in batch)
            {
                if (string.IsNullOrEmpty(dto.ReviewId) || dto.Rating == null || dto.Date == null)
                    continue;

                if (!seenIds.Add(dto.ReviewId))
                    continue;

                if (dto.Date < period.From)
                {
                    reachedDateBound = true;
                    continue;
                }

                if (dto.Date > period.To)
                    continue;

                reviews.Add(new RawReview(
                    ExternalId: dto.ReviewId,
                    Text: dto.Text ?? "",
                    Date: dto.Date.Value,
                    Stars: dto.Rating.Value,
                    BranchId: branch.BranchId,
                    AuthorName: dto.AuthorName,
                    AuthorPublicId: dto.AuthorId,
                    TextLanguage: dto.Language));
            }
        }

        return reachedDateBound;
    }

    private async Task ClickReviewsTabAsync(IPage page, CancellationToken ct)
    {
        // Google Maps SPA sometimes doesn't render tabs on first load — retry with reload
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var tab = await page.WaitForSelectorAsync(
                    "button[role='tab'][aria-label*='Отзывы'], button[role='tab'][aria-label*='Reviews']",
                    new PageWaitForSelectorOptions { Timeout = 10_000 });
                if (tab != null)
                {
                    await tab.ClickAsync();
                    _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' нажата");
                    await Task.Delay(Random.Shared.Next(2000, 3000), ct);
                    return;
                }
            }
            catch when (attempt == 0)
            {
                _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' не найдена, перезагружаю страницу...");
                await page.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _options.NavigationTimeoutMs
                });
                await Task.Delay(Random.Shared.Next(3000, 5000), ct);
                continue;
            }
            catch { /* fallback below */ }

            break;
        }

        // Fallback: try by tab text content
        var clicked = await page.EvaluateAsync<bool>("""
            () => {
                const tabs = document.querySelectorAll('button[role="tab"]');
                for (const tab of tabs) {
                    if (tab.textContent.includes('Отзывы') || tab.textContent.includes('Reviews')) {
                        tab.click();
                        return true;
                    }
                }
                return false;
            }
        """);

        if (clicked)
            _logger.LogDebug("[GMaps-Hybrid] Вкладка 'Отзывы' нажата (fallback)");
        else
            _logger.LogWarning("[GMaps-Hybrid] Вкладка 'Отзывы' не найдена");

        await Task.Delay(Random.Shared.Next(2000, 3000), ct);
    }

    private async Task SelectSortByNewestAsync(IPage page, CancellationToken ct)
    {
        var sortClicked = await page.EvaluateAsync<bool>("""
            () => {
                const btn = document.querySelector('button[aria-haspopup="listbox"]');
                if (btn) { btn.click(); return true; }
                return false;
            }
        """);

        if (!sortClicked)
        {
            _logger.LogWarning("[GMaps-Hybrid] Не удалось найти dropdown сортировки");
            return;
        }

        await Task.Delay(Random.Shared.Next(500, 1000), ct);

        var optionClicked = await page.EvaluateAsync<bool>("""
            () => {
                const items = document.querySelectorAll('[role="menuitemradio"]');
                for (const item of items) {
                    const text = item.textContent || '';
                    if (text.includes('новые') || text.includes('Newest')) {
                        item.click();
                        return true;
                    }
                }
                return false;
            }
        """);

        if (optionClicked)
            _logger.LogDebug("[GMaps-Hybrid] Сортировка 'Сначала новые' выбрана");
        else
            _logger.LogWarning("[GMaps-Hybrid] Не удалось выбрать 'Сначала новые'");

        await Task.Delay(Random.Shared.Next(2000, 3000), ct);
    }

    private async Task ScrollReviewsPanelAsync(IPage page)
    {
        await page.EvaluateAsync("""
            async () => {
                const c = document.querySelector('[role="main"] .m6QErb.DxyBCb')
                    || document.querySelector('.m6QErb.DxyBCb')
                    || document.querySelector('.m6QErb');
                if (!c) return;

                const target = c.scrollHeight;
                const current = c.scrollTop;
                const distance = target - current;
                if (distance <= 0) return;

                const steps = 3 + Math.floor(Math.random() * 4);
                for (let i = 1; i <= steps; i++) {
                    const progress = 1 - Math.pow(1 - i / steps, 2);
                    c.scrollTop = current + distance * progress;
                    await new Promise(r => setTimeout(r, 80 + Math.random() * 120));
                }
            }
        """);
    }
}
