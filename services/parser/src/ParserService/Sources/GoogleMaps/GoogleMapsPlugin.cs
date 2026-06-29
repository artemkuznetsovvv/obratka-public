using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Stealth;

namespace ParserService.Sources.GoogleMaps;

public class GoogleMapsPlugin : IReviewSourcePlugin
{
    private readonly IBrowserPool _browserPool;
    private readonly IProxyRotator _proxyRotator;
    private readonly IStealthConfigurator _stealthConfigurator;
    private readonly IPerSourceRateLimiter _rateLimiter;
    private readonly GoogleMapsOptions _options;
    private readonly ILogger<GoogleMapsPlugin> _logger;

    public GoogleMapsPlugin(
        IBrowserPool browserPool,
        IProxyRotator proxyRotator,
        IStealthConfigurator stealthConfigurator,
        IPerSourceRateLimiter rateLimiter,
        IOptions<GoogleMapsOptions> options,
        ILogger<GoogleMapsPlugin> logger)
    {
        _browserPool = browserPool;
        _proxyRotator = proxyRotator;
        _stealthConfigurator = stealthConfigurator;
        _rateLimiter = rateLimiter;
        _options = options.Value;
        _logger = logger;
    }

    public SourceType Source => SourceType.GoogleMaps;

    public async Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        _logger.LogInformation(
            "[GMaps] === Начинаю сбор отзывов === ExternalId={ExternalId}, период: {From} — {To}, режим: {Mode}",
            branch.ExternalId, period.From, period.To, _options.CollectionMode);

        await _rateLimiter.WaitAsync(SourceType.GoogleMaps, ct);

        IReadOnlyList<RawReview>? reviews = null;
        Exception? lastException = null;
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var proxy = await _proxyRotator.GetProxyAsync(SourceType.GoogleMaps, ct, tried);
            _logger.LogInformation("[GMaps] Попытка {Attempt}/{Max} для {ExternalId}, прокси={Proxy}",
                attempt, _options.MaxRetries, branch.ExternalId,
                proxy != null ? proxy.DisplayName : "без прокси");

            try
            {
                reviews = _options.CollectionMode switch
                {
                    GoogleMapsCollectionMode.BrowserScroll => await CollectViaBrowserAsync(branch, period, proxy, ct),
                    GoogleMapsCollectionMode.HybridScroll => await CollectViaHybridAsync(branch, period, proxy, ct),
                    _ => await CollectViaHybridAsync(branch, period, proxy, ct)
                };

                _logger.LogInformation("[GMaps] Попытка {Attempt} успешна: {Count} отзывов",
                    attempt, reviews.Count);
                break;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                lastException = ex;
                var reason = ClassifyFailure(ex);
                _logger.LogWarning(ex,
                    "[GMaps] Попытка {Attempt}/{Max} провалена через прокси {Proxy}: {Reason}. Повтор...",
                    attempt, _options.MaxRetries,
                    proxy != null ? proxy.DisplayName : "без прокси", reason);

                if (proxy != null)
                {
                    tried.Add(proxy);
                    await _proxyRotator.ReportFailureAsync(proxy, reason);
                }

                var backoff = (int)Math.Pow(2, attempt) * 1000;
                await Task.Delay(backoff, ct);
            }
            finally
            {
                if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
            }
        }

        if (reviews == null)
            throw lastException ?? new InvalidOperationException("Failed to collect Google Maps reviews");

        if (reviews.Count < 5)
            _logger.LogWarning("[GMaps] Мало отзывов: {Count} < 5 для {ExternalId}",
                reviews.Count, branch.ExternalId);

        _logger.LogInformation("[GMaps] === Сбор завершён === {ExternalId}: {Count} отзывов",
            branch.ExternalId, reviews.Count);
        return reviews;
    }

    public async Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        _logger.LogInformation("[GMaps] === Поиск организаций === запрос='{Query}', город='{City}'",
            request.Query, request.City);

        await _rateLimiter.WaitAsync(SourceType.GoogleMaps, ct);

        IReadOnlyList<SearchBranchResult>? results = null;
        Exception? lastException = null;
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var proxy = await _proxyRotator.GetProxyAsync(SourceType.GoogleMaps, ct, tried);
            _logger.LogInformation("[GMaps] Поиск: попытка {Attempt}/{Max}, прокси={Proxy}",
                attempt, _options.MaxRetries,
                proxy != null ? proxy.DisplayName : "без прокси");

            var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy, DisableHttp2: true), ct);
            try
            {
                await _stealthConfigurator.ApplyStealthAsync(browserContext, ct);
                var page = await browserContext.NewPageAsync();
                try
                {
                    var searchQuery = string.IsNullOrEmpty(request.City)
                        ? request.Query
                        : $"{request.Query} {request.City}";

                    // Google Maps search works with raw UTF-8 + spaces as '+'
                    // Uri.EscapeDataString percent-encodes Cyrillic which breaks search
                    var searchUrl = "https://www.google.com/maps/search/" + searchQuery.Replace(' ', '+');
                    _logger.LogDebug("[GMaps] Открываю поиск: {Url}", searchUrl);

                    await page.GotoAsync(searchUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _options.NavigationTimeoutMs
                    });
                    await GoogleMapsConsentHelper.DismissConsentIfNeededAsync(page, _logger, ct);

                    // Wait for either single-result redirect (URL → /maps/place/) or feed with cards.
                    // Fixes race where extraction runs before SPA settles into one of these states.
                    await WaitForSearchPageReadyAsync(page, ct);

                    results = await ExtractSearchResultsAsync(page);
                    _logger.LogDebug("[GMaps] Поиск: финальный URL={Url}, найдено={Count}",
                        page.Url, results.Count);
                    break;
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                lastException = ex;
                var reason = ClassifyFailure(ex);
                _logger.LogWarning(ex,
                    "[GMaps] Поиск: попытка {Attempt}/{Max} провалена через прокси {Proxy}: {Reason}. Повтор...",
                    attempt, _options.MaxRetries,
                    proxy != null ? proxy.DisplayName : "без прокси", reason);
                if (proxy != null)
                {
                    tried.Add(proxy);
                    await _proxyRotator.ReportFailureAsync(proxy, reason);
                }
                var backoff = (int)Math.Pow(2, attempt) * 1000;
                await Task.Delay(backoff, ct);
            }
            finally
            {
                await _browserPool.ReleaseAsync(browserContext);
                if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
            }
        }

        if (results == null)
            throw lastException ?? new InvalidOperationException("Failed to search GoogleMaps branches");

        _logger.LogInformation("[GMaps] === Поиск завершён === найдено {Count} для '{Query}'",
            results.Count, request.Query);
        return results;
    }

    // ---- Private: collection strategies ----

    private async Task<IReadOnlyList<RawReview>> CollectViaBrowserAsync(
        BranchTarget branch, DateRange period, ProxyInfo? proxy, CancellationToken ct)
    {
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy, DisableHttp2: true), ct);
        try
        {
            await _stealthConfigurator.ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);

            var placeUrl = BuildPlaceUrl(branch);
            var collector = new GoogleMapsBrowserCollector(_options, _logger);
            return await collector.CollectAllReviewsAsync(browserContext, placeUrl, branch, period, ct);
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
        }
    }

    private async Task<IReadOnlyList<RawReview>> CollectViaHybridAsync(
        BranchTarget branch, DateRange period, ProxyInfo? proxy, CancellationToken ct)
    {
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy, DisableHttp2: true), ct);
        try
        {
            await _stealthConfigurator.ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);

            var placeUrl = BuildPlaceUrl(branch);
            var collector = new GoogleMapsHybridCollector(_options, _logger);
            return await collector.CollectAllReviewsAsync(browserContext, placeUrl, branch, period, ct);
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
        }
    }

    // ---- Private: helpers ----

    /// <summary>
    /// Build place URL from BranchTarget.
    /// Priority: ExternalUrl → FID via data= param → fallback search.
    /// </summary>
    private static string BuildPlaceUrl(BranchTarget branch)
    {
        if (!string.IsNullOrEmpty(branch.ExternalUrl)
            && branch.ExternalUrl.Contains("google.com/maps", StringComparison.OrdinalIgnoreCase))
            return branch.ExternalUrl;

        // FID format: 0x{hex}:0x{hex} — open full place page via data= parameter.
        // Google resolves coordinates and loads complete card with Reviews tab.
        if (IsFid(branch.ExternalId))
            return $"https://www.google.com/maps/place/x/data=!3m1!4b1!4m2!3m1!1s{branch.ExternalId}";

        return "https://www.google.com/maps/search/" + branch.ExternalId.Replace(' ', '+');
    }

    private static bool IsFid(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Contains(':');

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException)
            return true;
        if (ex is PlaywrightException pe)
            return pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static ProxyFailureReason ClassifyFailure(Exception ex) => ex switch
    {
        TimeoutException => ProxyFailureReason.Timeout,
        PlaywrightException pe when pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.Timeout,
        PlaywrightException pe when pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.ConnectionError,
        _ => ProxyFailureReason.ServerError
    };

    // ---- Private: search ----

    private async Task WaitForSearchPageReadyAsync(IPage page, CancellationToken ct)
    {
        try
        {
            await page.WaitForFunctionAsync("""
                () => {
                    // Single-result redirect → place page with FID in URL
                    const url = window.location.href;
                    if (url.includes('/maps/place/') && /!1s0x[0-9a-f]+:0x[0-9a-f]+/i.test(url)) {
                        return true;
                    }
                    // Multi-result list → feed with at least one card
                    const feed = document.querySelector('[role="feed"]');
                    if (feed && feed.querySelector('.Nv2PK')) return true;
                    return false;
                }
            """, null, new PageWaitForFunctionOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("[GMaps] Поиск: страница не пришла в готовое состояние за 15с, URL={Url}",
                page.Url);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[GMaps] Поиск: страница не пришла в готовое состояние за 15с, URL={Url}",
                page.Url);
        }

        await Task.Delay(Random.Shared.Next(800, 1500), ct);
    }

    private async Task<IReadOnlyList<SearchBranchResult>> ExtractSearchResultsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("""
            () => {
                const results = [];
                const url = window.location.href;

                // Case 1: single-result redirect to /maps/place/.../!1s<fid>
                // Check URL FID first — most reliable signal that we're on a place page,
                // independent of any transient [role="feed"] state.
                const placeFidMatch = url.match(/!1s(0x[0-9a-f]+:0x[0-9a-f]+)/i);
                if (placeFidMatch && url.includes('/maps/place/')) {
                    const fid = placeFidMatch[1];
                    const nameEl = document.querySelector('h1');
                    const name = nameEl?.textContent?.trim() || '';

                    const ratingEl = document.querySelector('[role="img"][aria-label*="звездочн"], [role="img"][aria-label*="star"]');
                    let rating = null;
                    let reviewCount = null;
                    if (ratingEl) {
                        const label = ratingEl.getAttribute('aria-label') || '';
                        const rMatch = label.match(/([\d]+[,.][\d]+)/);
                        if (rMatch) rating = parseFloat(rMatch[1].replace(',', '.'));
                    }
                    // Число отзывов: Google сейчас держит его в span[role="img"][aria-label="N отзывов" / "N reviews"]
                    // рядом с h1 (раньше — на <button>, поэтому старый перебор button-ов промахивался).
                    const reviewCountEl = document.querySelector(
                        '[role="img"][aria-label*="отзыв"], [role="img"][aria-label*="review" i]');
                    if (reviewCountEl) {
                        const rcLabel = reviewCountEl.getAttribute('aria-label') || '';
                        const m = rcLabel.match(/([\d\s,.]+)/);
                        if (m) {
                            const n = parseInt(m[1].replace(/[\s,.]/g, ''));
                            if (!isNaN(n) && n > 0) reviewCount = n;
                        }
                    }

                    const addrEl = document.querySelector('[data-item-id="address"] .fontBodyMedium')
                        || document.querySelector('button[data-item-id="address"]');
                    const address = addrEl?.textContent?.trim() || '';

                    results.push({ fid, name, address, href: url, rating, reviewCount });
                    return JSON.stringify(results);
                }

                // Case 2: search returned a list of results
                const feed = document.querySelector('[role="feed"]');
                if (feed) {
                    const cards = feed.querySelectorAll('.Nv2PK');
                    for (const card of cards) {
                        const link = card.querySelector('a.hfpxzc');
                        if (!link) continue;

                        const href = link.getAttribute('href') || '';
                        const ariaLabel = link.getAttribute('aria-label') || '';

                        const fidMatch = href.match(/!1s(0x[0-9a-f]+:0x[0-9a-f]+)/i);
                        const fid = fidMatch ? fidMatch[1] : null;
                        if (!fid) continue;

                        const nameEl = card.querySelector('.qBF1Pd');
                        const name = nameEl?.textContent?.trim() || ariaLabel || '';

                        // Format: "4,6-звездочные Отзывов: 2 199" or "4.6 stars 2,199 Reviews"
                        const ratingEl = card.querySelector('[role="img"][aria-label]');
                        let rating = null;
                        let reviewCount = null;
                        if (ratingEl) {
                            const label = ratingEl.getAttribute('aria-label') || '';
                            const rMatch = label.match(/([\d]+[,.][\d]+)/);
                            if (rMatch) rating = parseFloat(rMatch[1].replace(',', '.'));
                            const cMatch = label.match(/(\d[\d\s,.]*\d)/g);
                            if (cMatch) {
                                reviewCount = parseInt(cMatch[cMatch.length - 1].replace(/[\s,.]/g, ''));
                            }
                        }

                        let address = '';
                        const w4els = card.querySelectorAll('.W4Efsd');
                        for (const el of w4els) {
                            if (el.querySelector('.W4Efsd')) continue;
                            const t = el.textContent || '';
                            if (!t.includes('·')) continue;
                            if (/Откроется|Открыто|Закрыто|Opens|Closes|Open/i.test(t)) continue;
                            const parts = t.split('·').map(p => p.trim()).filter(p => p.length > 0);
                            if (parts.length >= 2) {
                                address = parts[parts.length - 1];
                                break;
                            }
                        }

                        results.push({ fid, name, address, href, rating, reviewCount });
                        if (results.length >= 20) break;
                    }
                }

                return JSON.stringify(results);
            }
        """);

        var items = JsonSerializer.Deserialize<List<SearchResultJson>>(json) ?? [];

        return items
            .Where(r => !string.IsNullOrEmpty(r.Fid))
            .Select(r => new SearchBranchResult(
                Source: SourceType.GoogleMaps,
                ExternalId: r.Fid!,
                ExternalUrl: r.Href ?? "",
                Name: r.Name ?? "",
                Address: r.Address ?? "",
                Rating: r.Rating,
                ReviewCount: r.ReviewCount,
                // У Google карточка поиска показывает именно «N отзывов», т.е. ReviewCount
                // и так настоящее число отзывов с текстом → дублируем в RealReviewsCount.
                // Это нужно чтобы клиенты могли единообразно читать RealReviewsCount
                // независимо от источника.
                RealReviewsCount: r.ReviewCount))
            .ToList();
    }

    private record SearchResultJson(
        [property: JsonPropertyName("fid")] string? Fid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("href")] string? Href,
        [property: JsonPropertyName("rating")] double? Rating,
        [property: JsonPropertyName("reviewCount")] int? ReviewCount);
}
