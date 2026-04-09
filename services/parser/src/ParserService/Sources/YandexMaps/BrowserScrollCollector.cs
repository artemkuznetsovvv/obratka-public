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
            // --- Step 1: Set up response interception ---
            page.Response += (_, response) =>
            {
                if (!response.Url.Contains("/maps/api/business/fetchReviews"))
                    return;
                if (response.Status != 200)
                    return;

                _ = CaptureResponseAsync(response, interceptedResponses);
            };

            // --- Step 2: Optional warm-up ---
            if (_options.WarmUpSession)
            {
                await YandexSession.WarmUpAsync(page, _logger, ct);
            }

            // --- Step 3: Navigate to reviews page ---
            var reviewsUrl = BuildReviewsUrl(orgUrl);
            _logger.LogInformation("BrowserScroll: navigating to {Url}", reviewsUrl);

            await page.GotoAsync(reviewsUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.NavigationTimeoutMs
            });

            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Step 4: Handle captcha ---
            var html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                _logger.LogWarning("SmartCaptcha detected on reviews page");
                var captchaHandler = new SmartCaptchaHandler(_logger, _options);
                var solved = await captchaHandler.TrySolveCaptchaAsync(page, headful, ct);

                if (!solved)
                    throw new SmartCaptchaException(
                        "SmartCaptcha could not be solved. Try residential/mobile proxies or reduce request rate.");

                html = await page.ContentAsync();
                if (SmartCaptchaHandler.IsCaptchaPage(html))
                    throw new SmartCaptchaException(
                        "SmartCaptcha appeared again after solving. IP is likely flagged.");

                await Task.Delay(Random.Shared.Next(2000, 4000), ct);
            }

            // --- Step 5: Select sort by date ---
            await SelectSortByDateAsync(page, ct);

            // --- Step 6: Process initial intercepted responses ---
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
            var reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);

            _logger.LogDebug("Initial batch: {Count} reviews, hasMore={HasMore}", reviews.Count, hasMore);

            // --- Step 7: Scroll loop ---
            int consecutiveEmpty = 0;
            const int maxConsecutiveEmpty = 5;

            for (int attempt = 0; attempt < _options.MaxScrollAttempts && hasMore && !reachedDateBound; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = reviews.Count;

                await TriggerNextPageAsync(page, ct);

                // Wait for intercepted response to arrive (poll queue with timeout)
                // instead of a blind delay — prevents premature "empty" verdicts
                var waitDeadline = DateTime.UtcNow.AddMilliseconds(_options.DelayBetweenPagesMaxMs * 2);
                while (interceptedResponses.IsEmpty && DateTime.UtcNow < waitDeadline)
                {
                    await Task.Delay(300, ct);
                }

                // Human-like pause after response arrives
                await Task.Delay(Random.Shared.Next(
                    _options.DelayBetweenPagesMinMs,
                    _options.DelayBetweenPagesMaxMs + 1), ct);

                reachedDateBound = DrainQueue(interceptedResponses, reviews, seenIds, branch, period, ref hasMore);

                if (reviews.Count == beforeCount)
                {
                    consecutiveEmpty++;
                    _logger.LogDebug("Scroll attempt {Attempt}: no new reviews (empty streak: {Streak})",
                        attempt + 1, consecutiveEmpty);

                    if (consecutiveEmpty >= maxConsecutiveEmpty)
                    {
                        _logger.LogDebug("{Max} consecutive empty scrolls, stopping", maxConsecutiveEmpty);
                        break;
                    }
                }
                else
                {
                    consecutiveEmpty = 0;
                    _logger.LogDebug("Scroll attempt {Attempt}: total {Count} reviews", attempt + 1, reviews.Count);
                }
            }

            _logger.LogInformation("BrowserScroll: collected {Count} reviews for {BusinessId}",
                reviews.Count, branch.ExternalId);

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
            // Real API wraps payload in { "data": { "reviews": [...] } }
            var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(body, JsonOptions);
            var parsed = root?.Data;
            if (parsed?.Reviews != null)
            {
                queue.Enqueue(parsed);
                _logger.LogDebug("Intercepted fetchReviews response: {Count} reviews, hasMore={HasMore}",
                    parsed.Reviews.Count, parsed.HasMore);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture fetchReviews response");
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
        await page.EvaluateAsync(@"() => {
            const c = document.querySelector('.scroll__container');
            if (c) c.scrollTop = c.scrollHeight;
            else window.scrollTo(0, document.body.scrollHeight);
        }");
        _logger.LogDebug("Scrolled sidebar container to bottom");
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
