using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ParserService.Core.Models;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Collects reviews by scrolling Google Maps reviews tab and parsing DOM.
/// Simplest approach — no API interception needed.
/// Limitation: dates are relative ("год назад"), text may be auto-translated.
/// </summary>
internal sealed partial class GoogleMapsBrowserCollector
{
    private readonly GoogleMapsOptions _options;
    private readonly ILogger _logger;

    public GoogleMapsBrowserCollector(GoogleMapsOptions options, ILogger logger)
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

        var page = await browserContext.NewPageAsync();
        try
        {
            _logger.LogInformation("[GMaps-Browser] Начинаю сбор для {ExternalId} (URL: {Url})",
                branch.ExternalId, placeUrl);

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

            // --- Scroll loop ---
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                // Parse visible reviews from DOM
                var domReviews = await ExtractReviewsFromDomAsync(page);
                foreach (var dto in domReviews)
                {
                    if (string.IsNullOrEmpty(dto.ReviewId) || !seenIds.Add(dto.ReviewId))
                        continue;

                    if (dto.Rating == null)
                        continue;

                    // BrowserScroll mode has only relative dates — approximate
                    var date = dto.Date ?? DateTimeOffset.UtcNow;

                    if (date < period.From)
                        continue;
                    if (date > period.To)
                        continue;

                    reviews.Add(new RawReview(
                        ExternalId: dto.ReviewId,
                        Text: dto.Text ?? "",
                        Date: date,
                        Stars: dto.Rating.Value,
                        BranchId: branch.BranchId,
                        AuthorName: dto.AuthorName,
                        AuthorPublicId: dto.AuthorId,
                        TextLanguage: dto.Language));
                }

                var newReviews = reviews.Count - beforeCount;

                if (newReviews == 0)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug("[GMaps-Browser] Скролл #{N}: нет новых (пустых: {Streak}/{Max})",
                        attempt + 1, consecutiveEmpty, maxConsecutiveEmpty);

                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogInformation("[GMaps-Browser] {Max} пустых скроллов — останавливаюсь",
                            maxConsecutiveEmpty);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug("[GMaps-Browser] Скролл #{N}: +{New}, всего {Total}",
                        attempt + 1, newReviews, reviews.Count);
                }

                // Scroll down
                await ScrollReviewsPanelAsync(page);
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);
            }

            _logger.LogInformation("[GMaps-Browser] Сбор завершён: {ExternalId}, {Count} отзывов",
                branch.ExternalId, reviews.Count);

            return reviews;
        }
        finally
        {
            await page.CloseAsync();
        }
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
                    _logger.LogDebug("[GMaps-Browser] Вкладка 'Отзывы' нажата");
                    await Task.Delay(Random.Shared.Next(2000, 3000), ct);
                    return;
                }
            }
            catch when (attempt == 0)
            {
                _logger.LogDebug("[GMaps-Browser] Вкладка 'Отзывы' не найдена, перезагружаю страницу...");
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
            _logger.LogDebug("[GMaps-Browser] Вкладка 'Отзывы' нажата (fallback)");
        else
            _logger.LogWarning("[GMaps-Browser] Вкладка 'Отзывы' не найдена");

        await Task.Delay(Random.Shared.Next(2000, 3000), ct);
    }

    private async Task SelectSortByNewestAsync(IPage page, CancellationToken ct)
    {
        // Click sort dropdown
        var sortClicked = await page.EvaluateAsync<bool>("""
            () => {
                const btn = document.querySelector('button[aria-haspopup="listbox"]');
                if (btn) { btn.click(); return true; }
                return false;
            }
        """);

        if (!sortClicked)
        {
            _logger.LogWarning("[GMaps-Browser] Не удалось найти dropdown сортировки");
            return;
        }

        await Task.Delay(Random.Shared.Next(500, 1000), ct);

        // Click "Сначала новые" / "Newest"
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
            _logger.LogDebug("[GMaps-Browser] Сортировка 'Сначала новые' выбрана");
        else
            _logger.LogWarning("[GMaps-Browser] Не удалось выбрать 'Сначала новые'");

        await Task.Delay(Random.Shared.Next(2000, 3000), ct);
    }

    private async Task<IReadOnlyList<GoogleMapsReviewDto>> ExtractReviewsFromDomAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("""
            () => {
                const results = [];
                const containers = document.querySelectorAll('.jftiEf');

                for (const c of containers) {
                    const nameEl = c.querySelector('.d4r55');
                    const starsEl = c.querySelector('[role="img"][aria-label]');
                    const dateEl = c.querySelector('.rsqaWe');
                    const textEl = c.querySelector('.wiI7pd');
                    const reviewId = c.getAttribute('data-review-id') || '';

                    let rating = null;
                    if (starsEl) {
                        const m = starsEl.getAttribute('aria-label').match(/(\d)/);
                        if (m) rating = parseInt(m[1]);
                    }

                    results.push({
                        reviewId,
                        authorName: nameEl?.textContent?.trim() || '',
                        rating,
                        dateText: dateEl?.textContent?.trim() || '',
                        text: textEl?.textContent?.trim() || ''
                    });
                }

                return JSON.stringify(results);
            }
        """);

        var items = System.Text.Json.JsonSerializer.Deserialize<List<DomReviewJson>>(json) ?? [];

        return items
            .Where(r => !string.IsNullOrEmpty(r.ReviewId))
            .Select(r => new GoogleMapsReviewDto(
                ReviewId: r.ReviewId ?? "",
                Text: r.Text,
                Date: null, // DOM has only relative dates
                Rating: r.Rating,
                AuthorName: r.AuthorName,
                AuthorId: null, // not available from DOM
                Language: null))
            .ToList();
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

    private record DomReviewJson(
        [property: System.Text.Json.Serialization.JsonPropertyName("reviewId")] string? ReviewId,
        [property: System.Text.Json.Serialization.JsonPropertyName("authorName")] string? AuthorName,
        [property: System.Text.Json.Serialization.JsonPropertyName("rating")] int? Rating,
        [property: System.Text.Json.Serialization.JsonPropertyName("dateText")] string? DateText,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string? Text);
}
