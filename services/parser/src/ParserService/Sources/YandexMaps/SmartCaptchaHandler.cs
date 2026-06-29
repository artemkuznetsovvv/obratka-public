using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ParserService.Sources.YandexMaps;

/// <summary>
/// Detects and attempts to solve Yandex SmartCaptcha on a page.
/// Strategy: click the checkbox → wait for redirect back to original page.
/// If that fails and a captcha-solving service is configured, use it as fallback.
/// </summary>
internal sealed partial class SmartCaptchaHandler
{
    private readonly ILogger _logger;
    private readonly YandexMapsOptions _options;

    public SmartCaptchaHandler(ILogger logger, YandexMapsOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Returns true if the current page is a SmartCaptcha challenge.
    /// </summary>
    public static bool IsCaptchaPage(string html)
    {
        return html.Contains("CheckboxCaptcha", StringComparison.Ordinal)
            || html.Contains("SmartCaptcha", StringComparison.Ordinal)
            || html.Contains("captcha_smart", StringComparison.Ordinal)
            || html.Contains("\u0412\u044b \u043d\u0435 \u0440\u043e\u0431\u043e\u0442", StringComparison.Ordinal); // "Вы не робот"
    }

    /// <summary>
    /// Attempts to solve the captcha on the current page.
    /// Returns true if solved and page navigated away from captcha.
    /// </summary>
    public async Task<bool> TrySolveCaptchaAsync(IPage page, bool headful, CancellationToken ct)
    {
        _logger.LogWarning("SmartCaptcha detected, attempting to solve...");

        // Step 1: Try clicking the checkbox
        var solved = await TryClickCheckboxAsync(page, ct);

        if (solved)
        {
            _logger.LogInformation("SmartCaptcha solved via checkbox click");
            return true;
        }

        // Step 2: If headful — wait for manual solving (user sees the browser window)
        if (headful)
        {
            _logger.LogWarning(">>> Captcha requires manual solving. Solve it in the browser window. Waiting up to 120 seconds...");

            solved = await WaitForManualSolveAsync(page, timeoutSeconds: 120, ct);
            if (solved)
            {
                _logger.LogInformation("SmartCaptcha solved manually by user");
                return true;
            }
        }

        // Step 3: If we have a captcha service key, try the external solver
        if (!string.IsNullOrEmpty(_options.CaptchaSolverApiKey))
        {
            solved = await TrySolveViaExternalServiceAsync(page, ct);
            if (solved)
            {
                _logger.LogInformation("SmartCaptcha solved via external captcha service");
                return true;
            }
        }

        _logger.LogError("Failed to solve SmartCaptcha");
        return false;
    }

    private async Task<bool> WaitForManualSolveAsync(IPage page, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            try
            {
                var html = await page.ContentAsync();
                if (!IsCaptchaPage(html))
                    return true;
            }
            catch
            {
                // Page might be navigating — ignore and retry
            }
        }

        _logger.LogWarning("Manual captcha solve timed out after {Timeout}s", timeoutSeconds);
        return false;
    }

    private async Task<bool> TryClickCheckboxAsync(IPage page, CancellationToken ct)
    {
        try
        {
            // The checkbox button has id="js-button" and class="CheckboxCaptcha-Button"
            var button = await page.QuerySelectorAsync("#js-button")
                ?? await page.QuerySelectorAsync(".CheckboxCaptcha-Button");

            if (button is null)
            {
                _logger.LogDebug("Checkbox button not found on captcha page");
                return false;
            }

            // Simulate human-like mouse movement before clicking
            var box = await button.BoundingBoxAsync();
            if (box is not null)
            {
                // Move to a random point within the button
                var targetX = box.X + box.Width * (0.3 + Random.Shared.NextDouble() * 0.4);
                var targetY = box.Y + box.Height * (0.3 + Random.Shared.NextDouble() * 0.4);

                await page.Mouse.MoveAsync((float)targetX, (float)targetY,
                    new MouseMoveOptions { Steps = Random.Shared.Next(5, 15) });

                // Small delay before click (human behavior)
                await Task.Delay(Random.Shared.Next(200, 600), ct);

                await page.Mouse.ClickAsync((float)targetX, (float)targetY,
                    new MouseClickOptions { Delay = Random.Shared.Next(50, 150) });
            }
            else
            {
                await button.ClickAsync(new ElementHandleClickOptions
                {
                    Delay = Random.Shared.Next(50, 150)
                });
            }

            _logger.LogDebug("Clicked captcha checkbox, waiting for navigation...");

            // Wait for navigation away from captcha page (redirect to original URL)
            try
            {
                await page.WaitForURLAsync(
                    url => !url.Contains("/checkcaptcha") && !url.Contains("captcha"),
                    new PageWaitForURLOptions { Timeout = 15_000 });
            }
            catch (TimeoutException)
            {
                // URL didn't change, but maybe the page content changed
                // (some captchas redirect via JS or form submission)
            }

            // Wait for the new page to load
            await Task.Delay(Random.Shared.Next(2000, 4000), ct);

            // Check if we're still on captcha
            var html = await page.ContentAsync();
            if (IsCaptchaPage(html))
            {
                _logger.LogDebug("Still on captcha page after checkbox click — captcha likely escalated");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during checkbox captcha click");
            return false;
        }
    }

    private async Task<bool> TrySolveViaExternalServiceAsync(IPage page, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Attempting to solve captcha via external service (2Captcha)");

            // Extract the sitekey from captcha page
            var html = await page.ContentAsync();
            var sitekey = ExtractCaptchaKey(html);

            if (string.IsNullOrEmpty(sitekey))
            {
                _logger.LogWarning("Could not extract captcha sitekey from page");
                return false;
            }

            var pageUrl = page.Url;

            // Step 1: Submit captcha to solving service
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

            var submitUrl = $"https://2captcha.com/in.php" +
                $"?key={Uri.EscapeDataString(_options.CaptchaSolverApiKey!)}" +
                $"&method=yandex" +
                $"&sitekey={Uri.EscapeDataString(sitekey)}" +
                $"&pageurl={Uri.EscapeDataString(pageUrl)}" +
                $"&json=1";

            var submitResponse = await httpClient.GetStringAsync(submitUrl, ct);
            _logger.LogDebug("2Captcha submit response: {Response}", submitResponse);

            // Parse task ID from response like {"status":1,"request":"CAPTCHA_ID"}
            var taskIdMatch = TaskIdRegex().Match(submitResponse);
            if (!taskIdMatch.Success)
            {
                _logger.LogWarning("Failed to submit captcha to 2Captcha: {Response}", submitResponse);
                return false;
            }

            var taskId = taskIdMatch.Groups[1].Value;
            _logger.LogDebug("2Captcha task ID: {TaskId}", taskId);

            // Step 2: Poll for result
            var resultUrl = $"https://2captcha.com/res.php" +
                $"?key={Uri.EscapeDataString(_options.CaptchaSolverApiKey!)}" +
                $"&action=get" +
                $"&id={taskId}" +
                $"&json=1";

            for (int i = 0; i < 30; i++) // up to 150 seconds
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                var resultResponse = await httpClient.GetStringAsync(resultUrl, ct);
                _logger.LogDebug("2Captcha poll response: {Response}", resultResponse);

                if (resultResponse.Contains("CAPCHA_NOT_READY"))
                    continue;

                if (resultResponse.Contains("\"status\":1"))
                {
                    var tokenMatch = CaptchaTokenRegex().Match(resultResponse);
                    if (tokenMatch.Success)
                    {
                        var token = tokenMatch.Groups[1].Value;
                        _logger.LogDebug("Got captcha solution, applying...");

                        // Apply the token by calling the Yandex callback
                        var jsCode = "(function() {" +
                            "var form = document.getElementById('checkbox-captcha-form');" +
                            "if (form) {" +
                            "var input = document.createElement('input');" +
                            "input.type = 'hidden';" +
                            "input.name = 'smart-token';" +
                            "input.value = '" + token.Replace("'", "\\'") + "';" +
                            "form.appendChild(input);" +
                            "form.submit();" +
                            "}" +
                            "})()";
                        await page.EvaluateAsync(jsCode);

                        await Task.Delay(3000, ct);

                        var newHtml = await page.ContentAsync();
                        return !IsCaptchaPage(newHtml);
                    }
                }

                // Error from service
                _logger.LogWarning("2Captcha returned error: {Response}", resultResponse);
                return false;
            }

            _logger.LogWarning("2Captcha solve timed out after 150s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External captcha solving failed");
            return false;
        }
    }

    private static string? ExtractCaptchaKey(string html)
    {
        var match = CaptchaKeyRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""captchaKey["']?\s*[:=]\s*["']([^"']+)["']""")]
    private static partial Regex CaptchaKeyRegex();

    [GeneratedRegex(""""request"\s*:\s*"(\d+)"""")]
    private static partial Regex TaskIdRegex();

    [GeneratedRegex(""""request"\s*:\s*"([^"]+)"""")]
    private static partial Regex CaptchaTokenRegex();
}
