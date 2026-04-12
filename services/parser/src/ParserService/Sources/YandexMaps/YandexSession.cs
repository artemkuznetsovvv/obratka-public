using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ParserService.Sources.YandexMaps;

/// <summary>
/// Thrown when SmartCaptcha is detected and cannot be solved automatically.
/// Treated as a transient error by YandexMapsPlugin to trigger retry with IP rotation.
/// </summary>
internal sealed class SmartCaptchaException : Exception
{
    public SmartCaptchaException(string message) : base(message) { }
    public SmartCaptchaException(string message, Exception inner) : base(message, inner) { }
}

internal sealed partial class YandexSession : IAsyncDisposable
{
    public string CsrfToken { get; }
    public string SessionId { get; }
    public string ApiBaseUrl { get; }
    public string? RequestId { get; }
    public string Locale { get; }
    public IReadOnlyList<BrowserContextCookiesResult> Cookies { get; }
    public IBrowserContext BrowserContext { get; }

    private YandexSession(
        string csrfToken,
        string sessionId,
        string apiBaseUrl,
        string? requestId,
        string locale,
        IReadOnlyList<BrowserContextCookiesResult> cookies,
        IBrowserContext browserContext)
    {
        CsrfToken = csrfToken;
        SessionId = sessionId;
        ApiBaseUrl = apiBaseUrl;
        RequestId = requestId;
        Locale = locale;
        Cookies = cookies;
        BrowserContext = browserContext;
    }

    public static async Task<YandexSession> CreateAsync(
        IBrowserContext context,
        string organizationUrl,
        ILogger logger,
        YandexMapsOptions options,
        CancellationToken ct,
        bool headful = false)
    {
        var page = await context.NewPageAsync();

        try
        {
            logger.LogInformation("[Session] Создаю сессию для {Url}", organizationUrl);

            // --- Step 1: Warm-up session ---
            if (options.WarmUpSession)
            {
                logger.LogDebug("[Session] Прогрев сессии включён");
                await WarmUpAsync(page, logger, ct);
            }

            // --- Step 2: Intercept params from XHR ---
            logger.LogDebug("[Session] Настраиваю перехват параметров из API-запросов...");
            string? interceptedCsrfToken = null;
            string? interceptedSessionId = null;
            string? interceptedReqId = null;
            string? interceptedLocale = null;
            string? interceptedApiBaseUrl = null;

            await page.RouteAsync("**/maps/api/**", async route =>
            {
                try
                {
                    var uri = new Uri(route.Request.Url);
                    var qp = System.Web.HttpUtility.ParseQueryString(uri.Query);

                    interceptedCsrfToken ??= qp["csrfToken"];
                    interceptedSessionId ??= qp["sessionId"];
                    interceptedReqId ??= qp["reqId"];
                    interceptedLocale ??= qp["locale"];
                    interceptedApiBaseUrl ??= $"{uri.Scheme}://{uri.Host}";

                    if (interceptedCsrfToken is not null)
                        logger.LogDebug("Intercepted params from API request: csrf={HasCsrf}, sid={HasSid}",
                            interceptedCsrfToken is not null, interceptedSessionId is not null);
                }
                catch { /* ignore URI parsing errors */ }

                await route.ContinueAsync();
            });

            // --- Step 3: Navigate to org page ---
            logger.LogDebug("[Session] Перехожу на страницу организации: {Url}", organizationUrl);
            await page.GotoAsync(organizationUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Step 4: Check for SmartCaptcha ---
            logger.LogDebug("[Session] Страница загружена, текущий URL: {Url}. Проверяю капчу...", page.Url);
            var html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                logger.LogWarning("[Session] SmartCaptcha обнаружена на странице организации!");

                var captchaHandler = new SmartCaptchaHandler(logger, options);
                var solved = await captchaHandler.TrySolveCaptchaAsync(page, headful, ct);

                if (!solved)
                    throw new SmartCaptchaException(
                        "SmartCaptcha could not be solved. Try using residential/mobile proxies or reduce request rate.");

                // Refresh HTML after solving captcha
                html = await page.ContentAsync();

                // Second captcha check — sometimes Yandex shows another challenge
                if (SmartCaptchaHandler.IsCaptchaPage(html))
                    throw new SmartCaptchaException(
                        "SmartCaptcha appeared again after solving. IP is likely flagged.");
            }

            // --- Step 5: Extract csrfToken + session params ---
            logger.LogDebug("[Session] Капча не обнаружена. Извлекаю csrfToken и параметры сессии...");
            var csrfToken = ExtractCsrfToken(html);
            var sessionId = ExtractFromScript(html, SessionIdInScriptRegex());
            var reqId = ExtractFromScript(html, ReqIdInScriptRegex());

            logger.LogDebug("[Session] Из HTML: csrfToken={HasCsrf}, sessionId={HasSid}, reqId={HasReq}",
                csrfToken != null, sessionId != null, reqId != null);

            // Try JS evaluation for missing params
            if (csrfToken is null || sessionId is null)
            {
                logger.LogDebug("[Session] Параметры не полные, пробую JS-извлечение...");
                var jsParams = await TryExtractParamsViaJsAsync(page, logger);
                csrfToken ??= jsParams.CsrfToken;
                sessionId ??= jsParams.SessionId;
                reqId ??= jsParams.ReqId;

                logger.LogDebug("[Session] После JS: csrfToken={HasCsrf}, sessionId={HasSid}",
                    csrfToken != null, sessionId != null);
            }

            // Use intercepted values as fallback
            if (csrfToken is null || sessionId is null)
            {
                logger.LogDebug("[Session] Пробую перехваченные значения из API-запросов...");
            }
            csrfToken ??= interceptedCsrfToken;
            sessionId ??= interceptedSessionId;
            reqId ??= interceptedReqId;

            if (csrfToken is null)
            {
                var snippet = html.Length > 2000 ? html[..2000] : html;
                logger.LogError("[Session] Не удалось извлечь csrfToken! HTML (первые 2000 симв.): {Snippet}", snippet);
                throw new InvalidOperationException("Failed to extract csrfToken from page");
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                logger.LogWarning("[Session] sessionId не найден — генерирую fallback");
                sessionId = GenerateSessionId();
            }

            // --- Step 6: Determine API domain & locale ---
            var cookies = await context.CookiesAsync();

            var pageUri = new Uri(page.Url);
            var apiBaseUrl = interceptedApiBaseUrl ?? $"{pageUri.Scheme}://{pageUri.Host}";
            var locale = interceptedLocale ?? "ru_RU";

            logger.LogInformation(
                "[Session] Сессия создана: csrfToken={CsrfLen} симв., sessionId={SessionId}, apiBase={ApiBaseUrl}, locale={Locale}, cookies={CookieCount}",
                csrfToken.Length, sessionId, apiBaseUrl, locale, cookies.Count);

            return new YandexSession(csrfToken, sessionId, apiBaseUrl, reqId, locale, cookies, context);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Warms up the browser session by visiting yandex.ru first.
    /// This establishes cookies and trust before navigating to Maps.
    /// </summary>
    internal static async Task WarmUpAsync(IPage page, ILogger logger, CancellationToken ct)
    {
        logger.LogDebug("[WarmUp] Прогрев сессии — многоэтапный...");

        try
        {
            // --- Этап 1: Главная Яндекса —  установить cookies, trust score ---
            logger.LogDebug("[WarmUp] Этап 1: открываю yandex.ru...");
            await page.GotoAsync("https://yandex.ru/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15_000
            });

            logger.LogDebug("[WarmUp] yandex.ru загружен, URL: {Url}", page.Url);

            // Имитация чтения главной страницы
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // Несколько случайных движений мыши (как человек осматривает страницу)
            for (int i = 0; i < Random.Shared.Next(2, 5); i++)
            {
                await page.Mouse.MoveAsync(
                    Random.Shared.Next(100, 1200),
                    Random.Shared.Next(100, 600),
                    new MouseMoveOptions { Steps = Random.Shared.Next(5, 15) });
                await Task.Delay(Random.Shared.Next(300, 800), ct);
            }

            // Проверка капчи на главной
            var html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                logger.LogWarning("[WarmUp] SmartCaptcha на yandex.ru — IP сильно заблокирован!");
                return;
            }

            // Небольшой скролл вниз (как будто листаем новости)
            await page.Mouse.WheelAsync(0, Random.Shared.Next(200, 500));
            await Task.Delay(Random.Shared.Next(1000, 2000), ct);

            // --- Этап 2: Переход на Яндекс.Карты (через навигацию, не прямой URL) ---
            logger.LogDebug("[WarmUp] Этап 2: перехожу на yandex.ru/maps...");
            await page.GotoAsync("https://yandex.ru/maps/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15_000
            });

            logger.LogDebug("[WarmUp] Карты загружены, URL: {Url}", page.Url);
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // Движение мыши по карте
            await page.Mouse.MoveAsync(
                Random.Shared.Next(400, 1000),
                Random.Shared.Next(300, 600),
                new MouseMoveOptions { Steps = Random.Shared.Next(5, 10) });
            await Task.Delay(Random.Shared.Next(500, 1500), ct);

            // Проверка капчи на картах
            html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                logger.LogWarning("[WarmUp] SmartCaptcha на yandex.ru/maps — IP заблокирован!");
                return;
            }

            logger.LogDebug("[WarmUp] Прогрев завершён (2 этапа), cookies установлены");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WarmUp] Ошибка при прогреве сессии, продолжаю...");
        }
    }

    private record PageParams(string? CsrfToken, string? SessionId, string? ReqId);

    private static async Task<PageParams> TryExtractParamsViaJsAsync(IPage page, ILogger logger)
    {
        try
        {
            var result = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
                (function() {
                    var r = {};

                    function tryConfig(obj) {
                        if (!obj) return;
                        r.csrfToken = r.csrfToken || obj.csrfToken || null;
                        r.sessionId = r.sessionId || obj.sessionId || null;
                        r.reqId = r.reqId || obj.reqId || null;
                    }

                    if (window.maps_config) tryConfig(window.maps_config);

                    if (window.__PRELOADED_STATE__) {
                        var state = typeof window.__PRELOADED_STATE__ === 'string'
                            ? JSON.parse(window.__PRELOADED_STATE__)
                            : window.__PRELOADED_STATE__;
                        tryConfig(state);
                        if (state.config) tryConfig(state.config);
                    }

                    var scripts = document.querySelectorAll('script:not([src])');
                    for (var i = 0; i < scripts.length; i++) {
                        var text = scripts[i].textContent;
                        if (!text) continue;
                        if (!r.csrfToken) {
                            var m = text.match(/"csrfToken"\s*:\s*"([^"]+)"/);
                            if (m) r.csrfToken = m[1];
                        }
                        if (!r.sessionId) {
                            var m = text.match(/"sessionId"\s*:\s*"([^"]+)"/);
                            if (m) r.sessionId = m[1];
                        }
                        if (!r.reqId) {
                            var m = text.match(/"reqId"\s*:\s*"([^"]+)"/);
                            if (m) r.reqId = m[1];
                        }
                    }

                    var meta = document.querySelector('meta[name="csrf-token"]');
                    if (meta && !r.csrfToken) r.csrfToken = meta.getAttribute('content');

                    return r;
                })()
            """);

            var csrf = result.TryGetProperty("csrfToken", out var v1) ? v1.GetString() : null;
            var sid = result.TryGetProperty("sessionId", out var v2) ? v2.GetString() : null;
            var rid = result.TryGetProperty("reqId", out var v3) ? v3.GetString() : null;

            if (csrf != null || sid != null)
                logger.LogDebug("JS extraction: csrfToken={HasCsrf}, sessionId={HasSid}, reqId={HasReq}",
                    csrf != null, sid != null, rid != null);

            return new PageParams(csrf, sid, rid);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "JS evaluation for page params failed");
            return new PageParams(null, null, null);
        }
    }

    internal static string? ExtractCsrfToken(string html)
    {
        var match = ConfigViewRegex().Match(html);
        if (match.Success)
        {
            var tokenMatch = CsrfValueRegex().Match(match.Groups[1].Value);
            if (tokenMatch.Success)
                return tokenMatch.Groups[1].Value;
        }

        var globalMatch = CsrfInScriptRegex().Match(html);
        if (globalMatch.Success)
            return globalMatch.Groups[1].Value;

        return null;
    }

    public string BuildCookieHeader()
    {
        return string.Join("; ", Cookies.Select(c => $"{c.Name}={c.Value}"));
    }

    private static string? ExtractFromScript(string html, Regex regex)
    {
        var match = regex.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string GenerateSessionId()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r1 = Random.Shared.Next(100000, 999999);
        var r2 = Random.Shared.NextInt64(1000000000000, 9999999999999);
        return $"{ts}{r1}-{r2}-maps-front-production";
    }

    [GeneratedRegex("""<script[^>]*class\s*=\s*["']config-view["'][^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex ConfigViewRegex();

    [GeneratedRegex(""""csrfToken"\s*:\s*"([^"]+)"""")]
    private static partial Regex CsrfValueRegex();

    [GeneratedRegex("""<script[^>]*>(?:(?!</script>).)*?"csrfToken"\s*:\s*"([^"]+)"(?:(?!</script>).)*?</script>""", RegexOptions.Singleline)]
    private static partial Regex CsrfInScriptRegex();

    [GeneratedRegex("""<script[^>]*>(?:(?!</script>).)*?"sessionId"\s*:\s*"([^"]+)"(?:(?!</script>).)*?</script>""", RegexOptions.Singleline)]
    private static partial Regex SessionIdInScriptRegex();

    [GeneratedRegex("""<script[^>]*>(?:(?!</script>).)*?"reqId"\s*:\s*"([^"]+)"(?:(?!</script>).)*?</script>""", RegexOptions.Singleline)]
    private static partial Regex ReqIdInScriptRegex();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
