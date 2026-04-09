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
    public IReadOnlyList<BrowserContextCookiesResult> Cookies { get; }
    public IBrowserContext BrowserContext { get; }

    private YandexSession(
        string csrfToken,
        string sessionId,
        IReadOnlyList<BrowserContextCookiesResult> cookies,
        IBrowserContext browserContext)
    {
        CsrfToken = csrfToken;
        SessionId = sessionId;
        Cookies = cookies;
        BrowserContext = browserContext;
    }

    public static async Task<YandexSession> CreateAsync(
        IBrowserContext context,
        string organizationUrl,
        ILogger logger,
        YandexMapsOptions options,
        CancellationToken ct)
    {
        var page = await context.NewPageAsync();

        try
        {
            // --- Step 1: Warm-up session ---
            if (options.WarmUpSession)
            {
                await WarmUpAsync(page, logger, ct);
            }

            // --- Step 2: Intercept csrfToken from XHR ---
            string? interceptedCsrfToken = null;
            await page.RouteAsync("**/*", async route =>
            {
                var url = route.Request.Url;

                if (url.Contains("csrfToken=") && interceptedCsrfToken is null)
                {
                    var uri = new Uri(url);
                    var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var token = queryParams["csrfToken"];
                    if (!string.IsNullOrEmpty(token))
                    {
                        interceptedCsrfToken = token;
                        logger.LogDebug("Intercepted csrfToken from request: {Url}", url);
                    }
                }

                await route.ContinueAsync();
            });

            // --- Step 3: Navigate to org page ---
            logger.LogDebug("Navigating to {Url}", organizationUrl);
            await page.GotoAsync(organizationUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000
            });

            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // --- Step 4: Check for SmartCaptcha ---
            var html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                logger.LogWarning("SmartCaptcha detected on org page");

                var captchaHandler = new SmartCaptchaHandler(logger, options);
                var solved = await captchaHandler.TrySolveCaptchaAsync(page, ct);

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

            // --- Step 5: Extract csrfToken ---
            var csrfToken = ExtractCsrfToken(html);

            if (csrfToken is null)
            {
                logger.LogDebug("config-view script not found, trying JS evaluation");
                csrfToken = await TryExtractCsrfViaJsAsync(page, logger);
            }

            if (csrfToken is null && interceptedCsrfToken is not null)
            {
                logger.LogDebug("Using csrfToken intercepted from network request");
                csrfToken = interceptedCsrfToken;
            }

            if (csrfToken is null)
            {
                var snippet = html.Length > 2000 ? html[..2000] : html;
                logger.LogError("Failed to extract csrfToken. HTML snippet: {Snippet}", snippet);
                throw new InvalidOperationException("Failed to extract csrfToken from page");
            }

            // --- Step 6: Extract session info ---
            var cookies = await context.CookiesAsync();

            var sessionId = cookies
                .FirstOrDefault(c => c.Name == "Session_id")?.Value
                ?? cookies.FirstOrDefault(c => c.Name == "i")?.Value
                ?? "";

            logger.LogDebug("Session created: csrfToken length={CsrfLen}, sessionId length={SidLen}",
                csrfToken.Length, sessionId.Length);

            return new YandexSession(csrfToken, sessionId, cookies, context);
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
    private static async Task WarmUpAsync(IPage page, ILogger logger, CancellationToken ct)
    {
        logger.LogDebug("Warming up session — visiting yandex.ru");

        try
        {
            await page.GotoAsync("https://yandex.ru/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15_000
            });

            // Simulate a real user spending time on the main page
            await Task.Delay(Random.Shared.Next(2000, 5000), ct);

            // Simulate some mouse movement on main page
            await page.Mouse.MoveAsync(
                Random.Shared.Next(200, 800),
                Random.Shared.Next(200, 500),
                new MouseMoveOptions { Steps = Random.Shared.Next(3, 8) });

            await Task.Delay(Random.Shared.Next(500, 1500), ct);

            // Check for captcha even on main page
            var html = await page.ContentAsync();
            if (SmartCaptchaHandler.IsCaptchaPage(html))
            {
                logger.LogWarning("SmartCaptcha on yandex.ru main page — IP is heavily flagged");
                // Don't try to solve here, let it fail at the org page level
                // so the retry/IP rotation logic kicks in
            }
            else
            {
                logger.LogDebug("Warm-up completed, cookies established");
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Warm-up navigation failed, continuing anyway");
        }
    }

    private static async Task<string?> TryExtractCsrfViaJsAsync(IPage page, ILogger logger)
    {
        try
        {
            var token = await page.EvaluateAsync<string?>("""
                (function() {
                    if (window.maps_config && window.maps_config.csrfToken)
                        return window.maps_config.csrfToken;

                    if (window.__PRELOADED_STATE__) {
                        var state = typeof window.__PRELOADED_STATE__ === 'string'
                            ? JSON.parse(window.__PRELOADED_STATE__)
                            : window.__PRELOADED_STATE__;
                        if (state.csrfToken) return state.csrfToken;
                        if (state.config && state.config.csrfToken) return state.config.csrfToken;
                    }

                    var scripts = document.querySelectorAll('script:not([src])');
                    for (var i = 0; i < scripts.length; i++) {
                        var text = scripts[i].textContent;
                        if (text && text.indexOf('csrfToken') !== -1) {
                            var match = text.match(/"csrfToken"\s*:\s*"([^"]+)"/);
                            if (match) return match[1];
                        }
                    }

                    var meta = document.querySelector('meta[name="csrf-token"]');
                    if (meta) return meta.getAttribute('content');

                    return null;
                })()
            """);

            if (!string.IsNullOrEmpty(token))
            {
                logger.LogDebug("Extracted csrfToken via JS evaluation");
                return token;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "JS evaluation for csrfToken failed");
        }

        return null;
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

    [GeneratedRegex("""<script[^>]*class\s*=\s*["']config-view["'][^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex ConfigViewRegex();

    [GeneratedRegex(""""csrfToken"\s*:\s*"([^"]+)"""")]
    private static partial Regex CsrfValueRegex();

    [GeneratedRegex("""<script[^>]*>(?:(?!</script>).)*?"csrfToken"\s*:\s*"([^"]+)"(?:(?!</script>).)*?</script>""", RegexOptions.Singleline)]
    private static partial Regex CsrfInScriptRegex();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
