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

    private static readonly StealthProfile[] ProfileRotation =
        [StealthProfile.Moderate, StealthProfile.Minimal, StealthProfile.Full];

    /// При полном сборе ниже этого порога мы считаем результат подозрительным
    /// (вероятная тихая фильтрация по IP) и пробуем другой прокси, если есть свободные ретраи.
    private const int MinFullScanReviewsThreshold = 5;

    public async Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
    {
        _logger.LogInformation(
            "[YandexPlugin] === Начинаю сбор отзывов === BusinessId={BusinessId}, URL={Url}, период: {From} — {To}, режим: {Mode}",
            branch.ExternalId, branch.ExternalUrl, period.From, period.To, _options.CollectionMode);

        await _rateLimiter.WaitAsync(SourceType.YandexMaps, ct);
        _logger.LogDebug("[YandexPlugin] Rate limiter пройден");

        // Игнорируем branch.ExternalUrl: с фронта может прийти URL с query от страницы поиска
        // (?text=...&mode=search&ll=...&z=16). BuildReviewsUrl в коллекторе тупо добавляет
        // /reviews/ к концу, получается мусор типа .../?ll=...&z=16/reviews/ — Яндекс такой
        // URL не открывает как reviews-страницу, в результате собиралось 3 отзыва вместо 600.
        // Минимальный URL из businessId всегда корректен — Яндекс сам редиректит на полную
        // форму со slug. ExternalUrl остаётся в стартовом логе для диагностики.
        var orgUrl = $"https://yandex.ru/maps/org/{branch.ExternalId}/";

        var isFullScan = period.From == DateTimeOffset.MinValue;
        YandexCollectionOutcome? bestOutcome = null;
        Exception? lastException = null;
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var profile = ProfileRotation[(attempt - 1) % ProfileRotation.Length];
            _logger.LogInformation(
                "[YandexPlugin] Попытка {Attempt}/{MaxRetries} для {BusinessId} (stealth: {Profile})",
                attempt, _options.MaxRetries, branch.ExternalId, profile);

            var proxy = await _proxyRotator.GetProxyAsync(SourceType.YandexMaps, ct, tried);
            _logger.LogInformation("[YandexPlugin] Прокси: {Proxy}",
                proxy != null ? proxy.DisplayName : "без прокси");

            var browserContext = await _browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);
            _logger.LogDebug("[YandexPlugin] Browser context получен");

            try
            {
                await _stealthConfigurator.ApplyStealthAsync(browserContext, profile, ct);
                _logger.LogDebug("[YandexPlugin] Stealth-патчи применены (профиль: {Profile})", profile);

                var isHeadful = !_browserOptions.Headless;
                _logger.LogDebug("[YandexPlugin] Headful: {Headful}", isHeadful);

                YandexCollectionOutcome outcome;
                if (_options.CollectionMode == YandexCollectionMode.BrowserScroll)
                {
                    _logger.LogDebug("[YandexPlugin] Запускаю BrowserScrollCollector...");
                    var scrollCollector = new BrowserScrollCollector(_options, _logger);
                    outcome = await scrollCollector.CollectAllReviewsAsync(
                        browserContext, orgUrl, branch, period, isHeadful, ct);
                }
                else
                {
                    _logger.LogDebug("[YandexPlugin] Запускаю API-режим (создаю сессию)...");
                    await using var session = await YandexSession.CreateAsync(
                        browserContext, orgUrl, _logger, _options, ct, isHeadful);

                    var apiClient = new YandexReviewApiClient(_logger);
                    var collector = new YandexReviewCollector(apiClient, _options, _logger);

                    outcome = await collector.CollectAllReviewsAsync(session, branch, period, ct);
                }

                // Сохраняем лучший результат — следующая попытка может вернуть меньше
                // (например, провалится на transient ошибке после уже собранных страниц).
                if (bestOutcome is null || outcome.Reviews.Count > bestOutcome.Reviews.Count)
                    bestOutcome = outcome;

                _logger.LogInformation(
                    "[YandexPlugin] Попытка {Attempt} завершена: {Count} отзывов (hasMore={HasMore}, reachedDateBound={DateBound})",
                    attempt, outcome.Reviews.Count, outcome.HasMore, outcome.ReachedDateBound);

                // Подозрительно мало: полный сбор, Яндекс ещё мог отдавать страницы, в дату не упирались.
                // Самая вероятная причина — тихая фильтрация по IP. Пробуем другой прокси.
                var suspiciouslyFew = isFullScan
                    && outcome.Reviews.Count < MinFullScanReviewsThreshold
                    && outcome.HasMore
                    && !outcome.ReachedDateBound;

                if (suspiciouslyFew && attempt < _options.MaxRetries)
                {
                    _logger.LogWarning(
                        "[YandexPlugin] Попытка {Attempt}/{MaxRetries}: всего {Count} отзывов при полном сборе (hasMore=true) — подозрение на тихую фильтрацию IP. Пробую другой прокси.",
                        attempt, _options.MaxRetries, outcome.Reviews.Count);

                    if (proxy != null)
                        tried.Add(proxy);

                    var backoff = (int)Math.Pow(2, attempt) * 1000;
                    _logger.LogDebug("[YandexPlugin] Backoff: {Delay}мс перед следующей попыткой", backoff);
                    await Task.Delay(backoff, ct);
                    continue;
                }

                break;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                lastException = ex;
                var failureReason = ClassifyFailure(ex);
                _logger.LogWarning(ex,
                    "[YandexPlugin] Попытка {Attempt}/{MaxRetries} провалена для {BusinessId} через прокси {Proxy}: {Reason} (stealth: {Profile}). Повтор...",
                    attempt, _options.MaxRetries, branch.ExternalId,
                    proxy != null ? proxy.DisplayName : "без прокси", failureReason, profile);

                if (proxy != null)
                {
                    tried.Add(proxy);
                    await _proxyRotator.ReportFailureAsync(proxy, failureReason);
                }

                var backoff = (int)Math.Pow(2, attempt) * 1000;
                _logger.LogDebug("[YandexPlugin] Backoff: {Delay}мс перед следующей попыткой", backoff);
                await Task.Delay(backoff, ct);
            }
            finally
            {
                _logger.LogDebug("[YandexPlugin] Освобождаю browser context и прокси");
                await _browserPool.ReleaseAsync(browserContext);
                if (proxy != null) await _proxyRotator.ReleaseProxyAsync(proxy);
            }
        }

        if (bestOutcome is null)
            throw lastException ?? new InvalidOperationException("Failed to collect reviews");

        if (bestOutcome.Reviews.Count < MinFullScanReviewsThreshold)
            _logger.LogWarning(
                "[YandexPlugin] Мало отзывов: {Count} < {Min} для {BusinessId} (hasMore={HasMore}, reachedDateBound={DateBound})",
                bestOutcome.Reviews.Count, MinFullScanReviewsThreshold,
                branch.ExternalId, bestOutcome.HasMore, bestOutcome.ReachedDateBound);

        _logger.LogInformation("[YandexPlugin] === Сбор завершён === {BusinessId}: {Count} отзывов",
            branch.ExternalId, bestOutcome.Reviews.Count);
        return bestOutcome.Reviews;
    }

    public async Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        _logger.LogInformation("[YandexPlugin] === Поиск организаций === запрос='{Query}', город='{City}'",
            request.Query, request.City);

        await _rateLimiter.WaitAsync(SourceType.YandexMaps, ct);

        IReadOnlyList<SearchBranchResult>? results = null;
        Exception? lastException = null;
        var tried = new List<ProxyInfo>();

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            var proxy = await _proxyRotator.GetProxyAsync(SourceType.YandexMaps, ct, tried);
            _logger.LogInformation("[YandexPlugin] Поиск: попытка {Attempt}/{Max}, прокси={Proxy}",
                attempt, _options.MaxRetries,
                proxy != null ? proxy.DisplayName : "без прокси");

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
                    _logger.LogDebug("[YandexPlugin] Открываю поиск: {Url}", searchUrl);

                    await page.GotoAsync(searchUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30_000
                    });

                    _logger.LogDebug("[YandexPlugin] Страница поиска загружена, URL: {Url}", page.Url);
                    await Task.Delay(Random.Shared.Next(2000, 4000), ct);

                    _logger.LogDebug("[YandexPlugin] Извлекаю карточки организаций из DOM...");
                    results = await ExtractSearchResultsAsync(page, ct);
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
                    "[YandexPlugin] Поиск: попытка {Attempt}/{Max} провалена через прокси {Proxy}: {Reason}. Повтор...",
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
            throw lastException ?? new InvalidOperationException("Failed to search Yandex branches");

        _logger.LogInformation("[YandexPlugin] === Поиск завершён === найдено {Count} организаций для '{Query}'",
            results.Count, request.Query);
        return results;
    }

    private async Task<IReadOnlyList<SearchBranchResult>> ExtractSearchResultsAsync(
        IPage page, CancellationToken ct)
    {
        // Case 2: поиск с одним результатом редиректит сразу на карточку организации
        // URL вида /maps/org/{slug}/{businessId}/
        var currentUrl = page.Url;
        var directMatch = DirectOrgUrlRegex().Match(currentUrl);
        if (directMatch.Success)
        {
            _logger.LogDebug("[YandexPlugin] Case 2: редирект на карточку организации, извлекаю из single-page");
            var single = await ExtractSingleOrgAsync(page, directMatch.Groups[1].Value, ct);
            return single != null ? [single] : [];
        }

        // Case 1: обычный список результатов. Делаем один JS-проход — быстрее и устойчивее.
        var json = await page.EvaluateAsync<string>("""
            () => {
                const items = document.querySelectorAll('ul.search-list-view__list > li.search-snippet-view');
                const out = [];
                for (const li of items) {
                    try {
                        const nameLink = li.querySelector('a[href*="/maps/org/"]');
                        if (!nameLink) continue;
                        const href = nameLink.getAttribute('href') || '';
                        const m = href.match(/\/maps\/org\/[^\/]+\/(\d+)\//) || href.match(/\/maps\/org\/(\d+)/);
                        if (!m) continue;
                        const businessId = m[1];
                        const name = (nameLink.textContent || '').trim();

                        const ratingEl = li.querySelector('.business-rating-badge-view__rating-text');
                        const rating = ratingEl ? (ratingEl.textContent || '').trim() : null;

                        // ВАЖНО: в search-list карточке Яндекса виден ТОЛЬКО счётчик «N оценок»
                        // (.business-rating-with-text-view__count). Это число оценок (rating votes),
                        // НЕ число отзывов с текстом — настоящее число отзывов в карточке поиска
                        // не показывается. Кладём это значение в ReviewCount (исторический контракт
                        // «число рядом с рейтингом»). RealReviewsCount остаётся null — точный
                        // отзыв-каунт доступен только на странице организации.
                        const countEl = li.querySelector('.business-rating-with-text-view__count');
                        const reviewCountRaw = countEl ? (countEl.textContent || '').trim() : null;

                        const addrLink = li.querySelector('a[href*="/house/"]');
                        const address = addrLink ? (addrLink.textContent || '').trim() : '';

                        out.push({ businessId, href, name, rating, reviewCountRaw, address });
                    } catch (e) { /* skip bad card */ }
                }
                return JSON.stringify(out);
            }
        """);

        using var doc = JsonDocument.Parse(json);
        var results = new List<SearchBranchResult>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var businessId = el.GetProperty("businessId").GetString();
            if (string.IsNullOrEmpty(businessId)) continue;

            var href = el.GetProperty("href").GetString() ?? "";
            var name = el.GetProperty("name").GetString() ?? "";
            var address = el.GetProperty("address").GetString() ?? "";
            var ratingStr = el.GetProperty("rating").GetString();
            var reviewCountRaw = el.GetProperty("reviewCountRaw").GetString();

            double? rating = null;
            if (!string.IsNullOrEmpty(ratingStr)
                && double.TryParse(ratingStr.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
                rating = parsed;

            int? reviewCount = null;
            if (!string.IsNullOrEmpty(reviewCountRaw))
            {
                var digits = ReviewCountDigitsRegex().Replace(reviewCountRaw, "");
                if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var cnt))
                    reviewCount = cnt;
            }

            results.Add(new SearchBranchResult(
                Source: SourceType.YandexMaps,
                ExternalId: businessId,
                ExternalUrl: href.StartsWith("http") ? href : $"https://yandex.ru{href}",
                Name: name,
                Address: address,
                Rating: rating,
                ReviewCount: reviewCount));
        }

        return results;
    }

    private async Task<SearchBranchResult?> ExtractSingleOrgAsync(IPage page, string businessId, CancellationToken ct)
    {
        var json = await page.EvaluateAsync<string>("""
            () => {
                const h1 = document.querySelector('h1') || document.querySelector('[class*="card-title-view__title"]');
                const name = h1 ? (h1.textContent || '').trim() : '';

                const ratingEl = document.querySelector('.business-rating-badge-view__rating-text');
                const rating = ratingEl ? (ratingEl.textContent || '').trim() : null;

                // ReviewCount — берём из шапки рейтинга («N оценок» — это рейтинг-каунт,
                // НЕ отзыв-каунт). Поле сохранено как ReviewCount по историческому контракту.
                const countEl = document.querySelector('.business-rating-amount-view')
                    || document.querySelector('.business-header-rating-view__text');
                const reviewCountRaw = countEl ? (countEl.textContent || '').trim() : null;

                // RealReviewsCount — из бейджа таба «Отзывы». Класс ._name_reviews стабилен:
                // текст вида "Отзывы306", где 306 = настоящее число отзывов с текстом
                // (см. скриншот Скай8: 699 оценок vs 306 отзывов).
                const reviewsTab = document.querySelector('.tabs-select-view__title._name_reviews')
                    || document.querySelector('[class*="_name_reviews"]');
                const realReviewsCountRaw = reviewsTab ? (reviewsTab.textContent || '').trim() : null;

                const addrEl = document.querySelector('a[href*="/house/"]')
                    || document.querySelector('.business-contacts-view__address');
                const address = addrEl ? (addrEl.textContent || '').trim() : '';

                return JSON.stringify({ name, rating, reviewCountRaw, realReviewsCountRaw, address });
            }
        """);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";
        var address = root.GetProperty("address").GetString() ?? "";
        var ratingStr = root.GetProperty("rating").GetString();
        var reviewCountRaw = root.GetProperty("reviewCountRaw").GetString();
        var realReviewsCountRaw = root.TryGetProperty("realReviewsCountRaw", out var rrcEl)
            ? rrcEl.GetString()
            : null;

        double? rating = null;
        if (!string.IsNullOrEmpty(ratingStr)
            && double.TryParse(ratingStr.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
            rating = parsed;

        int? reviewCount = null;
        if (!string.IsNullOrEmpty(reviewCountRaw))
        {
            var digits = ReviewCountDigitsRegex().Replace(reviewCountRaw, "");
            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var cnt))
                reviewCount = cnt;
        }

        int? realReviewsCount = null;
        if (!string.IsNullOrEmpty(realReviewsCountRaw))
        {
            var digits = ReviewCountDigitsRegex().Replace(realReviewsCountRaw, "");
            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var cnt))
                realReviewsCount = cnt;
        }

        if (string.IsNullOrEmpty(name)) return null;

        return new SearchBranchResult(
            Source: SourceType.YandexMaps,
            ExternalId: businessId,
            ExternalUrl: page.Url,
            Name: name,
            Address: address,
            Rating: rating,
            ReviewCount: reviewCount,
            RealReviewsCount: realReviewsCount);
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

    [GeneratedRegex(@"/maps/org/(?:[^/]+/)?(\d+)")]
    private static partial Regex DirectOrgUrlRegex();

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex ReviewCountDigitsRegex();
}
