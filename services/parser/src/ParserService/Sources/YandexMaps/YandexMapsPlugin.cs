using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Stealth;

namespace ParserService.Sources.YandexMaps;

public partial class YandexMapsPlugin : IReviewSourcePlugin
{
    private readonly IBrowserPool _browserPool;
    private readonly IProxyRotator _proxyRotator;
    private readonly IStealthConfigurator _stealthConfigurator;
    private readonly IPerSourceRateLimiter _rateLimiter;
    private readonly YandexMapsOptions _options;
    private readonly BrowserPoolOptions _browserOptions;
    private readonly ILogger<YandexMapsPlugin> _logger;

    public YandexMapsPlugin(
        IBrowserPool browserPool,
        IProxyRotator proxyRotator,
        IStealthConfigurator stealthConfigurator,
        IPerSourceRateLimiter rateLimiter,
        IOptions<YandexMapsOptions> options,
        IOptions<BrowserPoolOptions> browserOptions,
        ILogger<YandexMapsPlugin> logger)
    {
        _browserPool = browserPool;
        _proxyRotator = proxyRotator;
        _stealthConfigurator = stealthConfigurator;
        _rateLimiter = rateLimiter;
        _options = options.Value;
        _browserOptions = browserOptions.Value;
        _logger = logger;
    }

    public SourceType Source => SourceType.YandexMaps;

    public async Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        _logger.LogInformation("Starting review collection for {BusinessId}", branch.ExternalId);

        await _rateLimiter.WaitAsync(SourceType.YandexMaps, ct);

        var proxy = await _proxyRotator.GetProxyAsync(SourceType.YandexMaps, ct);
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);

        try
        {
            await _stealthConfigurator.ApplyStealthAsync(browserContext, ct);

            var orgUrl = branch.ExternalUrl;

            if (string.IsNullOrEmpty(orgUrl))
                orgUrl = $"https://yandex.ru/maps/org/{branch.ExternalId}/";

            IReadOnlyList<RawReview>? reviews = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
            {
                try
                {
                    var isHeadful = !_browserOptions.Headless;

                    if (_options.CollectionMode == YandexCollectionMode.BrowserScroll)
                    {
                        var scrollCollector = new BrowserScrollCollector(_options, _logger);
                        reviews = await scrollCollector.CollectAllReviewsAsync(
                            browserContext, orgUrl, branch, period, isHeadful, ct);
                    }
                    else
                    {
                        await using var session = await YandexSession.CreateAsync(
                            browserContext, orgUrl, _logger, _options, ct, isHeadful);

                        var apiClient = new YandexReviewApiClient(_logger);
                        var collector = new YandexReviewCollector(apiClient, _options, _logger);

                        reviews = await collector.CollectAllReviewsAsync(session, branch, period, ct);
                    }

                    break;
                }
                catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed for {BusinessId}, retrying...",
                        attempt, _options.MaxRetries, branch.ExternalId);

                    if (proxy != null)
                        await _proxyRotator.ReportFailureAsync(proxy, ClassifyFailure(ex));

                    var backoff = (int)Math.Pow(2, attempt) * 1000;
                    await Task.Delay(backoff, ct);
                }
            }

            if (reviews == null)
                throw lastException ?? new InvalidOperationException("Failed to collect reviews");

            if (reviews.Count < 5)
                _logger.LogWarning("Only {Count} reviews collected for {BusinessId} — below minimum threshold",
                    reviews.Count, branch.ExternalId);

            _logger.LogInformation("Collected {Count} reviews for {BusinessId}", reviews.Count, branch.ExternalId);
            return reviews;
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
            if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    public async Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Searching branches for '{Query}' in '{City}'", request.Query, request.City);

        await _rateLimiter.WaitAsync(SourceType.YandexMaps, ct);

        var proxy = await _proxyRotator.GetProxyAsync(SourceType.YandexMaps, ct);
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);

        try
        {
            await _stealthConfigurator.ApplyStealthAsync(browserContext, ct);

            var page = await browserContext.NewPageAsync();

            try
            {
                var searchQuery = string.IsNullOrEmpty(request.City)
                    ? request.Query
                    : $"{request.Query} {request.City}";

                var searchUrl = $"https://yandex.ru/maps/?text={Uri.EscapeDataString(searchQuery)}";

                await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });

                await Task.Delay(Random.Shared.Next(2000, 4000), ct);

                var results = await ExtractSearchResultsAsync(page, ct);

                _logger.LogInformation("Found {Count} branches for '{Query}'", results.Count, request.Query);
                return results;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
            if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    private async Task<IReadOnlyList<SearchBranchResult>> ExtractSearchResultsAsync(
        IPage page, CancellationToken ct)
    {
        var results = new List<SearchBranchResult>();

        var cards = await page.QuerySelectorAllAsync("[class*='search-snippet-view']");

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var nameEl = await card.QuerySelectorAsync("[class*='search-business-snippet-view__title']");
                var name = nameEl != null ? (await nameEl.InnerTextAsync()).Trim() : "";

                var addressEl = await card.QuerySelectorAsync("[class*='search-business-snippet-view__address']");
                var address = addressEl != null ? (await addressEl.InnerTextAsync()).Trim() : "";

                var linkEl = await card.QuerySelectorAsync("a[href*='/org/']");
                var href = linkEl != null ? await linkEl.GetAttributeAsync("href") ?? "" : "";

                var businessId = ExtractBusinessIdFromUrl(href);
                if (string.IsNullOrEmpty(businessId))
                    continue;

                var externalUrl = href.StartsWith("http")
                    ? href
                    : $"https://yandex.ru{href}";

                var ratingEl = await card.QuerySelectorAsync("[class*='business-rating-badge-view__rating']");
                double? rating = null;
                if (ratingEl != null)
                {
                    var ratingText = await ratingEl.InnerTextAsync();
                    if (double.TryParse(ratingText.Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                        rating = parsed;
                }

                var reviewCountEl = await card.QuerySelectorAsync("[class*='business-rating-badge-view__rating-count']");
                int? reviewCount = null;
                if (reviewCountEl != null)
                {
                    var countText = await reviewCountEl.InnerTextAsync();
                    var digits = ReviewCountDigitsRegex().Replace(countText, "");
                    if (int.TryParse(digits, out var cnt))
                        reviewCount = cnt;
                }

                results.Add(new SearchBranchResult(
                    Source: SourceType.YandexMaps,
                    ExternalId: businessId,
                    ExternalUrl: externalUrl,
                    Name: name,
                    Address: address,
                    Rating: rating,
                    ReviewCount: reviewCount));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse a search card, skipping");
            }
        }

        return results;
    }

    private static string? ExtractBusinessIdFromUrl(string url)
    {
        var match = BusinessIdRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException or TimeoutException or SmartCaptchaException)
            return true;

        if (ex is PlaywrightException pe)
            return pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static ProxyFailureReason ClassifyFailure(Exception ex) => ex switch
    {
        SmartCaptchaException => ProxyFailureReason.SmartCaptcha,
        TimeoutException => ProxyFailureReason.Timeout,
        HttpRequestException => ProxyFailureReason.ConnectionError,
        PlaywrightException pe when pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.Timeout,
        PlaywrightException pe when pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.ConnectionError,
        _ when ex.Message.Contains("csrf", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.CsrfError,
        _ => ProxyFailureReason.ServerError
    };

    [GeneratedRegex(@"/org/[^/]*/(\d+)/|/org/(\d+)")]
    private static partial Regex BusinessIdRegex();

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex ReviewCountDigitsRegex();
}
