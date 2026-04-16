using System.Net;
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
                var citySlug = MapCityToSlug(request.City);
                var encodedQuery = Uri.EscapeDataString(request.Query);
                var searchUrl = $"https://2gis.ru/{citySlug}/search/{encodedQuery}";
                _logger.LogDebug("[2GIS] Открываю поиск: {Url}", searchUrl);

                await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
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
        catch (Exception ex) when (proxy != null)
        {
            await _proxyRotator.ReportFailureAsync(proxy, ClassifyFailure(ex));
            throw;
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
        var proxy = await _proxyRotator.GetProxyAsync(SourceType.TwoGis, ct);
        _logger.LogDebug("[2GIS] API-режим: прокси={Proxy}",
            proxy != null ? $"{proxy.Host}:{proxy.Port}" : "без прокси");

        try
        {
            var apiKey = await GetApiKeyAsync(proxy, ct);

            using var httpClient = CreateHttpClient(proxy);
            var apiClient = new TwoGisApiClient(httpClient, _logger);
            var collector = new TwoGisApiCollector(apiClient, _options, _logger);

            return await collector.CollectAllReviewsAsync(
                branch.ExternalId, apiKey, branch, period, ct);
        }
        catch (Exception ex) when (proxy != null)
        {
            var reason = ClassifyFailure(ex);
            _logger.LogWarning("[2GIS] API proxy {Host}:{Port} failure: {Reason}",
                proxy.Host, proxy.Port, reason);
            await _proxyRotator.ReportFailureAsync(proxy, reason);
            throw;
        }
        finally
        {
            if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
        }
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
        catch (Exception ex) when (proxy != null)
        {
            var reason = ClassifyFailure(ex);
            _logger.LogWarning("[2GIS] Proxy {Host}:{Port} failure: {Reason}",
                proxy.Host, proxy.Port, reason);
            await _proxyRotator.ReportFailureAsync(proxy, reason);
            throw;
        }
        finally
        {
            await _browserPool.ReleaseAsync(browserContext);
            if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    // ---- Private: API key management ----

    private async Task<string> GetApiKeyAsync(ProxyInfo? proxy, CancellationToken ct)
    {
        // 1. Конфиг
        if (!string.IsNullOrEmpty(_options.ReviewApiKey))
            return _options.ReviewApiKey;

        // 2. Кеш
        if (!string.IsNullOrEmpty(_cachedApiKey))
            return _cachedApiKey;

        // 3. Извлекаем из страницы 2GIS (через тот же прокси)
        await _apiKeyLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_cachedApiKey))
                return _cachedApiKey;

            _logger.LogInformation("[2GIS] Извлекаю API-ключ со страницы 2gis.ru (прокси: {Proxy})...",
                proxy != null ? $"{proxy.Host}:{proxy.Port}" : "без прокси");
            var key = await ExtractApiKeyFromPageAsync(proxy, ct);
            _cachedApiKey = key;
            _logger.LogInformation("[2GIS] API-ключ получен: {Key}", key);
            return key;
        }
        finally
        {
            _apiKeyLock.Release();
        }
    }

    private async Task<string> ExtractApiKeyFromPageAsync(ProxyInfo? proxy, CancellationToken ct)
    {
        var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);
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

    private static HttpClient CreateHttpClient(ProxyInfo? proxy)
    {
        if (proxy == null)
            return new HttpClient();

        var webProxy = new WebProxy(new Uri(proxy.Url));
        if (!string.IsNullOrEmpty(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        return new HttpClient(new SocketsHttpHandler { Proxy = webProxy, UseProxy = true });
    }

    // ---- Private: search ----

    private static string MapCityToSlug(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return "moscow";
        var lower = city.Trim().ToLowerInvariant();
        return lower switch
        {
            "москва" or "moscow" => "moscow",
            "санкт-петербург" or "спб" or "saint-petersburg" or "spb" => "spb",
            "новосибирск" or "novosibirsk" => "novosibirsk",
            "екатеринбург" or "yekaterinburg" or "ekaterinburg" => "ekaterinburg",
            "казань" or "kazan" => "kazan",
            "нижний новгород" or "nizhny novgorod" => "nizhny_novgorod",
            "челябинск" or "chelyabinsk" => "chelyabinsk",
            "самара" or "samara" => "samara",
            "омск" or "omsk" => "omsk",
            "ростов-на-дону" or "rostov" => "rostov",
            "уфа" or "ufa" => "ufa",
            "красноярск" or "krasnoyarsk" => "krasnoyarsk",
            "пермь" or "perm" => "perm",
            "воронеж" or "voronezh" => "voronezh",
            "волгоград" or "volgograd" => "volgograd",
            _ => lower.Replace(' ', '_')
        };
    }

    private async Task<IReadOnlyList<SearchBranchResult>> ExtractSearchResultsAsync(
        IPage page, CancellationToken ct)
    {
        // 2GIS: dynamic CSS-классы меняются между деплоями.
        // Стабильный якорь: a[href*="/firm/"]. Карточка = link.parentElement.parentElement (класс _1kf6gff на апр-2026).
        // Дочерние: [0]=название, [1]=категория, [2]="4.91177 оценок" слитно, [3]=адрес (+ "X филиалов" иногда).
        var json = await page.EvaluateAsync<string>("""
            () => {
                const results = [];
                const seen = new Set();
                const links = document.querySelectorAll('a[href*="/firm/"]');

                const cleanAddress = (s) => {
                    if (!s) return '';
                    // Удаляем zero-width chars и обрезаем хвост "N филиалов/филиал/филиала"
                    let t = s.replace(/[\u200B-\u200D\uFEFF]/g, '').trim();
                    t = t.replace(/\s*\d+\s+филиал\w*\s*$/i, '').trim();
                    return t;
                };

                for (const link of links) {
                    const href = link.getAttribute('href') || '';
                    const firmMatch = href.match(/\/firm\/(\d+)/);
                    if (!firmMatch) continue;

                    const firmId = firmMatch[1];
                    if (seen.has(firmId)) continue;

                    const card = link.parentElement?.parentElement;
                    if (!card || !card.children || card.children.length < 2) continue;

                    const name = (link.textContent || '').trim();
                    if (!name || name.length > 200) continue;
                    seen.add(firmId);

                    let rating = null;
                    let reviewCount = null;
                    let address = '';

                    for (const ch of card.children) {
                        const t = (ch.textContent || '').trim();
                        if (!t) continue;

                        // Рейтинг + кол-во: "4.91177 оценок" — разделителя нет, рейтинг всегда X.X в начале
                        if (rating === null) {
                            const rm = t.match(/^(\d[.,]\d)(.*)$/);
                            if (rm) {
                                rating = parseFloat(rm[1].replace(',', '.'));
                                const cm = rm[2].match(/(\d[\d\s\u00A0]*)\s*(?:оцен|отзыв|review)/i);
                                if (cm) reviewCount = parseInt(cm[1].replace(/[\s\u00A0]/g, ''));
                                continue;
                            }
                        }

                        // Адрес: содержит запятую, не часы/статус
                        if (!address && t.includes(',') && !/^закрыт|^открыт|\d{1,2}:\d{2}/i.test(t)) {
                            address = cleanAddress(t);
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

    private static ProxyFailureReason ClassifyFailure(Exception ex) => ex switch
    {
        TimeoutException => ProxyFailureReason.Timeout,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
            => ProxyFailureReason.ServerError,
        HttpRequestException => ProxyFailureReason.ConnectionError,
        PlaywrightException pe when pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.Timeout,
        PlaywrightException pe when pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase)
            => ProxyFailureReason.ConnectionError,
        _ => ProxyFailureReason.ServerError
    };

    private record SearchResultJson(
        [property: System.Text.Json.Serialization.JsonPropertyName("firmId")] string? FirmId,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("address")] string? Address,
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("rating")] double? Rating,
        [property: System.Text.Json.Serialization.JsonPropertyName("reviewCount")] int? ReviewCount);
}
