using Microsoft.Playwright;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Handles Google Cookie Consent dialog ("Прежде чем перейти к Google").
/// Must be called after every navigation to a Google domain.
/// </summary>
internal static class GoogleMapsConsentHelper
{
    /// <summary>
    /// Detect and dismiss Google consent page if present.
    /// Safe to call even if no consent dialog is shown — returns quickly.
    /// </summary>
    public static async Task DismissConsentIfNeededAsync(IPage page, ILogger logger, CancellationToken ct)
    {
        // Check if we're on a consent page (URL or DOM indicator)
        var url = page.Url;
        var isConsentPage = url.Contains("consent.google")
            || url.Contains("/m?continue=");

        if (!isConsentPage)
        {
            // Also check for inline consent overlay (sometimes appears without redirect)
            var hasOverlay = await page.EvaluateAsync<bool>("""
                () => {
                    // Consent form or overlay
                    const form = document.querySelector('form[action*="consent"]');
                    if (form) return true;
                    // Cookie dialog
                    const dialog = document.querySelector('[role="dialog"]');
                    if (dialog && dialog.textContent.includes('cookie')) return true;
                    return false;
                }
            """);

            if (!hasOverlay) return;
        }

        logger.LogDebug("[GMaps] Обнаружен Google Consent диалог, закрываю...");

        // Try clicking "Принять все" / "Accept all" button
        var clicked = await page.EvaluateAsync<bool>("""
            () => {
                // Strategy 1: button with known text
                const buttons = document.querySelectorAll('button');
                for (const btn of buttons) {
                    const text = (btn.textContent || '').trim();
                    if (/Принять все|Accept all|Akzeptieren|Tout accepter|Aceptar todo/i.test(text)) {
                        btn.click();
                        return true;
                    }
                }

                // Strategy 2: form with consent action — submit first form
                const form = document.querySelector('form[action*="consent"]');
                if (form) {
                    const submitBtn = form.querySelector('button[type="submit"], input[type="submit"]');
                    if (submitBtn) { submitBtn.click(); return true; }
                    form.submit();
                    return true;
                }

                // Strategy 3: aria-label based
                const acceptBtn = document.querySelector(
                    'button[aria-label*="Принять"], button[aria-label*="Accept"], button[aria-label*="Agree"]'
                );
                if (acceptBtn) { acceptBtn.click(); return true; }

                return false;
            }
        """);

        if (clicked)
        {
            logger.LogInformation("[GMaps] Google Consent принят");
            // Wait for redirect back to maps
            try
            {
                await page.WaitForURLAsync("**/maps/**", new PageWaitForURLOptions { Timeout = 10_000 });
            }
            catch
            {
                // May already be on maps page if consent was inline overlay
            }

            await Task.Delay(Random.Shared.Next(1000, 2000), ct);
        }
        else
        {
            logger.LogWarning("[GMaps] Consent диалог обнаружен, но кнопку 'Принять' не удалось нажать");
        }
    }
}
