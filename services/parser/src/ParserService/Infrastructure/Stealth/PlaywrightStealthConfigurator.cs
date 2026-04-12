using Microsoft.Playwright;

namespace ParserService.Infrastructure.Stealth;

public class PlaywrightStealthConfigurator : IStealthConfigurator
{
    private readonly ILogger<PlaywrightStealthConfigurator> _logger;

    public PlaywrightStealthConfigurator(ILogger<PlaywrightStealthConfigurator> logger)
    {
        _logger = logger;
    }

    public async Task ApplyStealthAsync(IBrowserContext browserContext, CancellationToken ct)
    {
        await browserContext.AddInitScriptAsync(StealthScript);
        _logger.LogDebug("Stealth patches applied to browser context");
    }

    private const string StealthScript = """
        // Hide webdriver flag
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

        // Override plugins to look real
        Object.defineProperty(navigator, 'plugins', {
            get: () => [1, 2, 3, 4, 5]
        });

        // Override languages
        Object.defineProperty(navigator, 'languages', {
            get: () => ['ru-RU', 'ru', 'en-US', 'en']
        });

        // Chrome runtime mock
        window.chrome = { runtime: {}, loadTimes: function(){}, csi: function(){} };

        // Permissions query override
        const originalQuery = window.navigator.permissions.query;
        window.navigator.permissions.query = (parameters) =>
            parameters.name === 'notifications'
                ? Promise.resolve({ state: Notification.permission })
                : originalQuery(parameters);

        // WebGL vendor/renderer override
        const getParameter = WebGLRenderingContext.prototype.getParameter;
        WebGLRenderingContext.prototype.getParameter = function(parameter) {
            if (parameter === 37445) return 'Intel Inc.';
            if (parameter === 37446) return 'Intel Iris OpenGL Engine';
            return getParameter.call(this, parameter);
        };

        // Canvas fingerprint noise
        const toDataURL = HTMLCanvasElement.prototype.toDataURL;
        HTMLCanvasElement.prototype.toDataURL = function(type) {
            if (type === 'image/png' && this.width > 16 && this.height > 16) {
                const ctx = this.getContext('2d');
                if (ctx) {
                    const imageData = ctx.getImageData(0, 0, 1, 1);
                    imageData.data[0] = imageData.data[0] ^ 1;
                    ctx.putImageData(imageData, 0, 0);
                }
            }
            return toDataURL.apply(this, arguments);
        };
        """;
}
