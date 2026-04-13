using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Stealth;

namespace ParserService.Sources.TwoGis;

public partial class TwoGisPlugin : IReviewSourcePlugin
{
    private readonly IBrowserPool _browserPool;
    private readonly IProxyRotator _proxyRotator;
    private readonly IStealthConfigurator _stealthConfigurator;
    private readonly IPerSourceRateLimiter _rateLimiter;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwoGisOptions _options;
    private readonly ILogger<TwoGisPlugin> _logger;

    /// <summary>
    /// Кешированный API-ключ — извлекается из страницы 2GIS один раз.
    /// </summary>
    private string? _cachedApiKey;
    private readonly SemaphoreSlim _apiKeyLock = new(1, 1);

    public TwoGisPlugin(
        IBrowserPool browserPool,
        IProxyRotator proxyRotator,
        IStealthConfigurator stealthConfigurator,
        IPerSourceRateLimiter rateLimiter,
        IHttpClientFactory httpClientFactory,
        IOptions<TwoGisOptions> options,
        ILogger<TwoGisPlugin> logger)
    {
        _browserPool = browserPool;
        _proxyRotator = proxyRotator;
        _stealthConfigurator = stealthConfigurator;
        _rateLimiter = rateLimiter;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public SourceType Source => SourceType.TwoGis;

    public async Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        _logger.LogInformation(
            "[2GIS] === Начинаю сбор отзывов === FirmId={FirmId}, период: {From} — {To}, режим: {Mode}",
            branch.ExternalId, period.From, period.To, _options.CollectionMode);

        await _rateLimiter.WaitAsync(SourceType.TwoGis, ct);

        IReadOnlyList<RawReview>? reviews = null;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            _logger.LogInformation("[2GIS] Попытка {Attempt}/{Max} для {FirmId}",
                attempt, _options.MaxRetries, branch.ExternalId);

            try
            {
                reviews = _options.CollectionMode switch
                {
                    TwoGisCollectionMode.Api => await CollectViaApiAsync(branch, period, ct),
                    TwoGisCollectionMode.BrowserScroll => await CollectViaBrowserAsync(branch, period, ct),
                    _ => await CollectViaApiAsync(branch, period, ct)
                };

                _logger.LogInformation("[2GIS] Попытка {Attempt} успешна: {Count} отзывов",
                    attempt, reviews.Count);
                break;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "[2GIS] Попытка {Attempt}/{Max} провалена: {Message}. Повтор...",
                    attempt, _options.MaxRetries, ex.Message);

                // При ошибке API-ключа — сбрасываем кеш
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized })
                {
                    _logger.LogWarning("[2GIS] Сброс кешированного API-ключа");
                    _cachedApiKey = null;
                }

                var backoff = (int)Math.Pow(2, attempt) * 1000;
                await Task.Delay(backoff, ct);
            }
        }

        if (reviews == null)
            throw lastException ?? new InvalidOperationException("Failed to collect 2GIS reviews");

        if (reviews.Count < 5)
            _logger.LogWarning("[2GIS] Мало отзывов: {Count} < 5 для {FirmId}",
                reviews.Count, branch.ExternalId);

        _logger.LogInformation("[2GIS] === Сбор завершён === {FirmId}: {Count} отзывов",
            branch.ExternalId, reviews.Count);
        return reviews;
    }

    public async Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        _logger.LogInformation("[2GIS] === Поиск организаций === запрос='{Query}', город='{City}'",
            request.Query, request.City);

        await _rateLimiter.WaitAsync(SourceType.TwoGis, ct);

        // Catalog API поиска: catalog.api.2gis.ru/3.0/items
        // Пока используем browser-based поиск
        var proxy = await _proxyRotator.GetProxyAsync(SourceType.TwoGis, ct);
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

                var searchUrl = $"https://2gis.ru/search/{Uri.EscapeDataString(searchQuery)}";
                _logger.LogDebug("[2GIS] Открываю поиск: {Url}", searchUrl);

                await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = _options.NavigationTimeoutMs
                });
                await Task.Delay(Random.Shared.Next(2000, 4000), ct);

                var results = await ExtractSearchResultsAsync(page, ct);
                _logger.LogInformation("[2GIS] === Поиск завершён === найдено {Count} для '{Query}'",
                    results.Count, request.Query);
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

    // ---- Private: collection strategies ----

    private async Task<IReadOnlyList<RawReview>> CollectViaApiAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        var apiKey = await GetApiKeyAsync(ct);
        var httpClient = _httpClientFactory.CreateClient("2gis");
        var apiClient = new TwoGisApiClient(httpClient, _logger);
        var collector = new TwoGisApiCollector(apiClient, _options, _logger);

        return await collector.CollectAllReviewsAsync(
            branch.ExternalId, apiKey, branch, period, ct);
    }

    private async Task<IReadOnlyList<RawReview>> CollectViaBrowserAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        var proxy = await _proxyRotator.GetProxyAsync(SourceType.TwoGis, ct);
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);
        try
        {
            await _stealthConfigurator.ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);
            var collector = new TwoGisBrowserCollector(_options, _logger);
            return await collector.CollectAllReviewsAsync(
                browserContext, branch.ExternalId, branch, period, ct);
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
            if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    // ---- Private: API key management ----

    private async Task<string> GetApiKeyAsync(CancellationToken ct)
    {
        // 1. Конфиг
        if (!string.IsNullOrEmpty(_options.ReviewApiKey))
            return _options.ReviewApiKey;

        // 2. Кеш
        if (!string.IsNullOrEmpty(_cachedApiKey))
            return _cachedApiKey;

        // 3. Извлекаем из страницы 2GIS
        await _apiKeyLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_cachedApiKey))
                return _cachedApiKey;

            _logger.LogInformation("[2GIS] Извлекаю API-ключ со страницы 2gis.ru...");
            var key = await ExtractApiKeyFromPageAsync(ct);
            _cachedApiKey = key;
            _logger.LogInformation("[2GIS] API-ключ получен: {Key}", key);
            return key;
        }
        finally
        {
            _apiKeyLock.Release();
        }
    }

    private async Task<string> ExtractApiKeyFromPageAsync(CancellationToken ct)
    {
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(null), ct);
        try
        {
            var page = await browserContext.NewPageAsync();
            try
            {
                await page.GotoAsync("https://2gis.ru/", new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _options.NavigationTimeoutMs
                });

                var key = await page.EvaluateAsync<string?>("""
                    () => {
                        for (const s of document.querySelectorAll('script')) {
                            const text = s.textContent || '';
                            const match = text.match(/"reviewApiKey"\s*:\s*"([^"]+)"/);
                            if (match) return match[1];
                        }
                        return null;
                    }
                """);

                if (string.IsNullOrEmpty(key))
                    throw new InvalidOperationException(
                        "Could not extract reviewApiKey from 2gis.ru page. Check if DOM structure changed.");

                return key;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
        }
    }

    // ---- Private: search ----

    private async Task<IReadOnlyList<SearchBranchResult>> ExtractSearchResultsAsync(
        IPage page, CancellationToken ct)
    {
        // 2GIS dynamic CSS — используем JS для извлечения данных из карточек
        var json = await page.EvaluateAsync<string>("""
            () => {
                const results = [];
                // Карточки результатов поиска — ищем ссылки с /firm/ в href
                const links = document.querySelectorAll('a[href*="/firm/"]');
                const seen = new Set();

                for (const link of links) {
                    const href = link.getAttribute('href') || '';
                    const firmMatch = href.match(/\/firm\/(\d+)/);
                    if (!firmMatch) continue;

                    const firmId = firmMatch[1];
                    if (seen.has(firmId)) continue;
                    seen.add(firmId);

                    // Ищем ближайший контейнер карточки
                    const card = link.closest('[data-name]') || link.closest('article')
                        || link.parentElement?.parentElement?.parentElement;
                    if (!card) continue;

                    const name = link.textContent?.trim() || '';
                    if (!name || name.length > 200) continue;

                    // Адрес — обычно следующий текстовый элемент
                    const allText = card.querySelectorAll('*');
                    let address = '';
                    let rating = null;
                    let reviewCount = null;

                    for (const el of allText) {
                        const t = (el.textContent || '').trim();
                        // Рейтинг — число от 1.0 до 5.0
                        if (!rating && /^\d\.\d$/.test(t)) {
                            rating = parseFloat(t);
                        }
                        // Кол-во отзывов — "123 отзыва" или "1.2K отзывов"
                        const revMatch = t.match(/^(\d[\d\s]*)\s*(отзыв|review)/i);
                        if (revMatch) {
                            reviewCount = parseInt(revMatch[1].replace(/\s/g, ''));
                        }
                    }

                    results.push({
                        firmId,
                        name,
                        address,
                        url: 'https://2gis.ru' + href,
                        rating,
                        reviewCount
                    });

                    if (results.length >= 20) break;
                }
                return JSON.stringify(results);
            }
        """);

        var items = System.Text.Json.JsonSerializer.Deserialize<List<SearchResultJson>>(json)
            ?? [];

        return items.Select(r => new SearchBranchResult(
            Source: SourceType.TwoGis,
            ExternalId: r.FirmId ?? "",
            ExternalUrl: r.Url ?? "",
            Name: r.Name ?? "",
            Address: r.Address ?? "",
            Rating: r.Rating,
            ReviewCount: r.ReviewCount
        )).ToList();
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException or TimeoutException)
            return true;
        if (ex is PlaywrightException pe)
            return pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private record SearchResultJson(
        [property: System.Text.Json.Serialization.JsonPropertyName("firmId")] string? FirmId,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("address")] string? Address,
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("rating")] double? Rating,
        [property: System.Text.Json.Serialization.JsonPropertyName("reviewCount")] int? ReviewCount);
}
