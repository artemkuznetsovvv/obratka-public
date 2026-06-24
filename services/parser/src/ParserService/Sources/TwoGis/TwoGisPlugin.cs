using System.Collections.Concurrent;
using System.Net;
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
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var proxy = await _proxyRotator.GetProxyAsync(SourceType.TwoGis, ct, tried);
            _logger.LogInformation("[2GIS] Попытка {Attempt}/{Max} для {FirmId}, прокси={Proxy}",
                attempt, _options.MaxRetries, branch.ExternalId,
                proxy != null ? proxy.DisplayName : "без прокси");

            try
            {
                reviews = _options.CollectionMode switch
                {
                    TwoGisCollectionMode.Api => await CollectViaApiAsync(branch, period, proxy, ct),
                    TwoGisCollectionMode.BrowserScroll => await CollectViaBrowserAsync(branch, period, proxy, ct),
                    _ => await CollectViaApiAsync(branch, period, proxy, ct)
                };

                _logger.LogInformation("[2GIS] Попытка {Attempt} успешна: {Count} отзывов",
                    attempt, reviews.Count);
                break;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex, ct))
            {
                lastException = ex;
                var reason = ClassifyFailure(ex);
                _logger.LogWarning(ex,
                    "[2GIS] Попытка {Attempt}/{Max} провалена через прокси {Proxy}: {Reason}. Повтор...",
                    attempt, _options.MaxRetries,
                    proxy != null ? proxy.DisplayName : "без прокси", reason);

                if (proxy != null)
                {
                    tried.Add(proxy);
                    await _proxyRotator.ReportFailureAsync(proxy, reason);
                }

                // При ошибке API-ключа — сбрасываем кеш
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized })
                {
                    _logger.LogWarning("[2GIS] Сброс кешированного API-ключа");
                    _cachedApiKey = null;
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

        IReadOnlyList<SearchBranchResult>? results = null;
        Exception? lastException = null;
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var proxy = await _proxyRotator.GetProxyAsync(SourceType.TwoGis, ct, tried);
            _logger.LogInformation("[2GIS] Поиск: попытка {Attempt}/{Max}, прокси={Proxy}",
                attempt, _options.MaxRetries,
                proxy != null ? proxy.DisplayName : "без прокси");

            var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);
            try
            {
                await _stealthConfigurator.ApplyStealthAsync(browserContext, ct);
                var page = await browserContext.NewPageAsync();
                try
                {
                    // 2GIS-овский catalog.api.2gis.ru/3.0/markers/clustered возвращает для каждой
                    // карточки два разных счётчика:
                    //   reviews.general_review_count            — отзывы с текстом
                    //   reviews.general_review_count_with_stars — все оценки (звёзды без текста)
                    // DOM-карточка показывает только with_stars («N оценок»), отзыв-каунт нигде
                    // не виден на UI 2ГИС. Поэтому перехватываем API и кладём настоящий
                    // отзыв-каунт в отдельное поле RealReviewsCount, не трогая ReviewCount.
                    // (Раньше пробовал override-ить ReviewCount, но клиенты по нему привязаны как
                    // к «популярности» — см. SearchBranchResult.RealReviewsCount xml-doc.)
                    var realReviewsByFirmId = new ConcurrentDictionary<string, int>();
                    page.Response += (_, response) =>
                    {
                        if (!response.Url.Contains("catalog.api.2gis.ru/3.0/markers/clustered")) return;
                        if (response.Status != 200) return;
                        _ = CaptureMarkersResponseAsync(response, realReviewsByFirmId);
                    };

                    // 2GIS-овский citySlug в URL ненадёжен (n_novgorod vs nizhny_novgorod
                    // vs nizhniy_novgorod — разные города ломают разный mapping). Полнотекст
                    // 2gis сам разрешает локацию из строки запроса: "Surf Coffee нижний новгород"
                    // через /moscow/search/ возвращает правильный нижегородский филиал.
                    // Платим за это тем, что иногда в выдачу затёсывается 1 карточка из current-region
                    // (moscow) — UI всё равно показывает чекбоксы per branch, пользователь её снимет.
                    var fullQuery = string.IsNullOrWhiteSpace(request.City)
                        ? request.Query
                        : $"{request.Query} {request.City}";
                    var encodedQuery = Uri.EscapeDataString(fullQuery);
                    var searchUrl = $"https://2gis.ru/moscow/search/{encodedQuery}";
                    _logger.LogDebug("[2GIS] Открываю поиск: {Url}", searchUrl);

                    await page.GotoAsync(searchUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = _options.NavigationTimeoutMs
                    });
                    await Task.Delay(Random.Shared.Next(2000, 4000), ct);

                    var domResults = await ExtractSearchResultsAsync(page, ct);
                    results = AttachRealReviewsCount(domResults, realReviewsByFirmId);
                    break;
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex, ct))
            {
                lastException = ex;
                var reason = ClassifyFailure(ex);
                _logger.LogWarning(ex,
                    "[2GIS] Поиск: попытка {Attempt}/{Max} провалена через прокси {Proxy}: {Reason}. Повтор...",
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
            throw lastException ?? new InvalidOperationException("Failed to search 2GIS branches");

        _logger.LogInformation("[2GIS] === Поиск завершён === найдено {Count} для '{Query}'",
            results.Count, request.Query);
        return results;
    }

    // ---- Private: collection strategies ----

    private async Task<IReadOnlyList<RawReview>> CollectViaApiAsync(
        BranchTarget branch, DateRange period, ProxyInfo? proxy, CancellationToken ct)
    {
        var apiKey = await GetApiKeyAsync(proxy, ct);

        using var httpClient = CreateHttpClient(proxy);
        var apiClient = new TwoGisApiClient(httpClient, _logger);
        var collector = new TwoGisApiCollector(apiClient, _options, _logger);

        return await collector.CollectAllReviewsAsync(
            branch.ExternalId, apiKey, branch, period, ct);
    }

    private async Task<IReadOnlyList<RawReview>> CollectViaBrowserAsync(
        BranchTarget branch, DateRange period, ProxyInfo? proxy, CancellationToken ct)
    {
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
            await _stealthConfigurator.ApplyStealthAsync(browserContext, ct);
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

    private HttpClient CreateHttpClient(ProxyInfo? proxy)
    {
        // Без явного Timeout HttpClient берёт дефолт .NET = 100с и валится на медленных
        // прокси / больших страницах ("The request was canceled due to the configured
        // HttpClient.Timeout of 100 seconds elapsing"). Берём из TwoGisOptions.
        var timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        if (proxy == null)
            return new HttpClient { Timeout = timeout };

        var webProxy = new WebProxy(new Uri(proxy.Url));
        if (!string.IsNullOrEmpty(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        return new HttpClient(new SocketsHttpHandler { Proxy = webProxy, UseProxy = true })
        {
            Timeout = timeout
        };
    }

    // ---- Private: search ----

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

                // DOM-чистка адреса от бейджа "N филиалов".
                // 2GIS рендерит этот бейдж как <a href="/<city>/branches/<brandId>">N филиалов</a>
                // ВНУТРИ того же div'а с адресом, поэтому innerText захватывает и адрес, и бейдж.
                // Решение: клонируем child, удаляем все <a href*="/branches/">, читаем textContent.
                // Второй рубеж (zero-width chars + regex fallback) — в C# TwoGisAddressSanitizer.Clean,
                // на случай если 2GIS поменяет href/класс/структуру.
                const cleanAddressFromDom = (el) => {
                    const clone = el.cloneNode(true);
                    clone.querySelectorAll('a[href*="/branches/"]').forEach(a => a.remove());
                    return (clone.textContent || '').trim();
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
                            address = cleanAddressFromDom(ch);
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
            Address: TwoGisAddressSanitizer.Clean(r.Address),
            Rating: r.Rating,
            ReviewCount: r.ReviewCount
        )).ToList();
    }

    // "Внешняя ошибка" = всё, что зависит от сети/прокси/целевого сайта и может пройти
    // со следующей попытки. НЕ ретраим только реальную отмену снаружи (ct) и баги кода.
    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        // Настоящая отмена задачи снаружи (shutdown / таймаут задачи в PG) — не ретраим.
        if (ct.IsCancellationRequested)
            return false;

        return ex switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            System.IO.IOException => true,
            System.Net.Sockets.SocketException => true,
            // HttpClient.Timeout бросает TaskCanceledException (inner TimeoutException),
            // а НЕ TimeoutException. Раз ct не отменён — это таймаут запроса, ретраим.
            OperationCanceledException => true,
            PlaywrightException pe =>
                pe.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || pe.Message.Contains("net::", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static ProxyFailureReason ClassifyFailure(Exception ex) => ex switch
    {
        TimeoutException => ProxyFailureReason.Timeout,
        OperationCanceledException => ProxyFailureReason.Timeout, // HttpClient.Timeout
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }
            => ProxyFailureReason.ServerError,
        HttpRequestException => ProxyFailureReason.ConnectionError,
        System.IO.IOException or System.Net.Sockets.SocketException => ProxyFailureReason.ConnectionError,
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

    /// <summary>
    /// Парсит ответ catalog.api.2gis.ru/3.0/markers/clustered и складывает
    /// firmId → general_review_count (отзывы с текстом — для поля
    /// <see cref="SearchBranchResult.RealReviewsCount"/>).
    /// Один поиск может триггерить несколько запросов (зум/смещение карты) —
    /// аккумулируем все.
    /// </summary>
    private async Task CaptureMarkersResponseAsync(
        IResponse response,
        ConcurrentDictionary<string, int> realReviewsByFirmId)
    {
        try
        {
            var body = await response.TextAsync();
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("result", out var result)) return;
            if (!result.TryGetProperty("items", out var items)) return;
            if (items.ValueKind != JsonValueKind.Array) return;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id)) continue;

                // item.id формата "<firmId>_<hash>"; firmId — числовая часть до подчёркивания.
                var firmId = id.Split('_', 2)[0];
                if (string.IsNullOrEmpty(firmId)) continue;

                if (!item.TryGetProperty("reviews", out var reviews)) continue;
                if (!reviews.TryGetProperty("general_review_count", out var countEl)) continue;
                if (countEl.ValueKind != JsonValueKind.Number) continue;

                realReviewsByFirmId[firmId] = countEl.GetInt32();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[2GIS] Не удалось распарсить markers/clustered для RealReviewsCount");
        }
    }

    /// <summary>
    /// Добавляет <see cref="SearchBranchResult.RealReviewsCount"/> к DOM-результатам
    /// из аккумулированной API-карты. <see cref="SearchBranchResult.ReviewCount"/> не
    /// трогаем — там «N оценок» из DOM-карточки, нужно для совместимости.
    /// </summary>
    private List<SearchBranchResult> AttachRealReviewsCount(
        IReadOnlyList<SearchBranchResult> domResults,
        ConcurrentDictionary<string, int> realReviewsByFirmId)
    {
        if (realReviewsByFirmId.IsEmpty)
        {
            _logger.LogDebug("[2GIS] markers/clustered не успел отдать данные — RealReviewsCount останется null");
            return domResults.ToList();
        }

        int filled = 0, missing = 0;
        var result = new List<SearchBranchResult>(domResults.Count);
        foreach (var r in domResults)
        {
            if (!string.IsNullOrEmpty(r.ExternalId)
                && realReviewsByFirmId.TryGetValue(r.ExternalId, out var realCount))
            {
                filled++;
                result.Add(r with { RealReviewsCount = realCount });
            }
            else
            {
                missing++;
                result.Add(r); // RealReviewsCount остаётся null
            }
        }
        _logger.LogDebug(
            "[2GIS] RealReviewsCount: заполнено {Filled}, без данных {Missing} (из {Total})",
            filled, missing, domResults.Count);
        return result;
    }
}
