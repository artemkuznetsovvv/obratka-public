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
        => await ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);

    public async Task ApplyStealthAsync(
        IBrowserContext browserContext, StealthProfile profile, CancellationToken ct)
    {
        var script = profile switch
        {
            StealthProfile.Minimal => MinimalScript,
            StealthProfile.Moderate => MinimalScript + ModerateScript,
            StealthProfile.Full => MinimalScript + ModerateScript + FullScript,
            _ => MinimalScript + ModerateScript
        };

        await browserContext.AddInitScriptAsync(script);
        _logger.LogDebug("Stealth patches applied (profile: {Profile})", profile);
    }

    /// <summary>
    /// Webdriver flag, navigator props, chrome runtime, permissions.
    /// Proven safe — no known detection by Yandex.
    /// </summary>
    private const string MinimalScript = """
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

        """;

    /// <summary>
    /// WebGL vendor/renderer + canvas fingerprint noise.
    /// Stable, tested with Yandex.
    /// </summary>
    private const string ModerateScript = """
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

    /// <summary>
    /// AudioContext noise + font enumeration masking.
    /// Experimental — may trigger Yandex SmartCaptcha detection.
    /// </summary>
    private const string FullScript = """
        // AudioContext fingerprint noise
        const origGetFloatFrequencyData = AnalyserNode.prototype.getFloatFrequencyData;
        AnalyserNode.prototype.getFloatFrequencyData = function(array) {
            origGetFloatFrequencyData.call(this, array);
            for (let i = 0; i < array.length; i++) {
                array[i] += (Math.random() - 0.5) * 0.001;
            }
        };

        const origCreateOscillator = AudioContext.prototype.createOscillator;
        AudioContext.prototype.createOscillator = function() {
            const osc = origCreateOscillator.call(this);
            const origConnect = osc.connect.bind(osc);
            osc.connect = function(dest) {
                if (dest instanceof AnalyserNode) {
                    const gain = this.context.createGain();
                    gain.gain.value = 1 + (Math.random() - 0.5) * 0.0001;
                    origConnect(gain);
                    gain.connect(dest);
                    return dest;
                }
                return origConnect(dest);
            };
            return osc;
        };

        if (typeof OfflineAudioContext !== 'undefined') {
            const origRenderbuffer = OfflineAudioContext.prototype.startRendering;
            OfflineAudioContext.prototype.startRendering = function() {
                return origRenderbuffer.call(this).then(function(buffer) {
                    const channel = buffer.getChannelData(0);
                    for (let i = 0; i < channel.length; i++) {
                        channel[i] += (Math.random() - 0.5) * 0.0001;
                    }
                    return buffer;
                });
            };
        }

        // Font enumeration masking
        const origMeasureText = CanvasRenderingContext2D.prototype.measureText;
        const baseFonts = ['monospace', 'sans-serif', 'serif'];
        const knownFonts = new Set([
            'Arial', 'Verdana', 'Times New Roman', 'Georgia', 'Courier New',
            'Trebuchet MS', 'Tahoma', 'Segoe UI', 'Roboto', 'Helvetica'
        ]);
        CanvasRenderingContext2D.prototype.measureText = function(text) {
            const result = origMeasureText.call(this, text);
            const font = this.font || '';
            const isProbe = text.length <= 3 && !baseFonts.some(f => font.includes(f));
            const isUnknownFont = !knownFonts.has(font.replace(/["']/g, '').split(',')[0].trim());
            if (isProbe && isUnknownFont) {
                const fallback = this.font;
                this.font = font.replace(/^[^,]+,?\s*/, '') || '16px sans-serif';
                const fallbackResult = origMeasureText.call(this, text);
                this.font = fallback;
                return fallbackResult;
            }
            return result;
        };

        """;
}
