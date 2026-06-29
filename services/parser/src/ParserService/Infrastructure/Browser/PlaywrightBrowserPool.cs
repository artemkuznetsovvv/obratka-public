using System.Collections.Concurrent;
using Microsoft.Playwright;
using ParserService.Infrastructure.Proxy;

namespace ParserService.Infrastructure.Browser;

public class BrowserPoolOptions
{
    public const string SectionName = "Browser";

    /// <summary>
    /// Run browser without GUI. Set to false to see the browser window (useful for debugging).
    /// </summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Max одновременно живых browser-контекстов. Должно быть ≥ Workers:MaxConcurrent × 2
    /// (worst-case task держит 2 контекста: API-key probe + основной сбор), иначе воркеры
    /// будут блокироваться на browser-семафоре.
    /// </summary>
    public int MaxContexts { get; set; } = 6;
}

public class PlaywrightBrowserPool : IBrowserPool, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly SemaphoreSlim _playwrightInitLock = new(1, 1);
    private readonly ConcurrentDictionary<bool, Lazy<Task<BrowserInstance>>> _browsers = new();
    private readonly ILogger<PlaywrightBrowserPool> _logger;
    private readonly BrowserPoolOptions _options;

    private IPlaywright? _playwright;

    /// <summary>
    /// Common desktop viewports — weighted towards popular resolutions.
    /// </summary>
    private static readonly ViewportSize[] Viewports =
    [
        new() { Width = 1920, Height = 1080 },
        new() { Width = 1920, Height = 1080 },
        new() { Width = 1366, Height = 768 },
        new() { Width = 1536, Height = 864 },
        new() { Width = 1440, Height = 900 },
        new() { Width = 1680, Height = 1050 },
        new() { Width = 2560, Height = 1440 },
    ];

    public PlaywrightBrowserPool(
        ILogger<PlaywrightBrowserPool> logger,
        Microsoft.Extensions.Options.IOptions<BrowserPoolOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        var capacity = Math.Max(1, _options.MaxContexts);
        _semaphore = new SemaphoreSlim(capacity, capacity);
    }

    public async Task<IBrowserContext> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);

        try
        {
            var disableHttp2 = options?.DisableHttp2 ?? false;
            var instance = await GetOrCreateBrowserAsync(disableHttp2, ct);

            var viewport = Viewports[Random.Shared.Next(Viewports.Length)];
            var ua = BuildUserAgent(instance.Version);

            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = ua,
                Locale = "ru-RU",
                TimezoneId = "Europe/Moscow",
                ViewportSize = viewport,
                ScreenSize = new ScreenSize { Width = viewport.Width, Height = viewport.Height },
                DeviceScaleFactor = Random.Shared.Next(0, 10) < 3 ? 2 : 1,
            };

            if (options?.Proxy is { } proxy)
            {
                contextOptions.Proxy = new Microsoft.Playwright.Proxy
                {
                    Server = proxy.Url,
                    Username = proxy.Username,
                    Password = proxy.Password
                };
            }

            var context = await instance.Browser.NewContextAsync(contextOptions);

            _logger.LogDebug(
                "[BrowserPool] Context создан: UA={UA}, viewport={W}x{H}, proxy={HasProxy}, h2={Http2}",
                ua[^30..], viewport.Width, viewport.Height, options?.Proxy != null, !disableHttp2);
            return context;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    public async Task ReleaseAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private string BuildUserAgent(string browserVersion)
    {
        // Use real Chrome version from the launched browser to match TLS/JA3 fingerprint
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36";
    }

    private async Task<BrowserInstance> GetOrCreateBrowserAsync(bool disableHttp2, CancellationToken ct)
    {
        await EnsurePlaywrightAsync(ct);

        var lazy = _browsers.GetOrAdd(disableHttp2,
            key => new Lazy<Task<BrowserInstance>>(() => LaunchBrowserAsync(key)));
        return await lazy.Value;
    }

    private async Task EnsurePlaywrightAsync(CancellationToken ct)
    {
        if (_playwright != null) return;

        await _playwrightInitLock.WaitAsync(ct);
        try
        {
            if (_playwright != null) return;

            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("[BrowserPool] Инициализация Playwright...");
            _playwright = await Playwright.CreateAsync();
        }
        finally
        {
            _playwrightInitLock.Release();
        }
    }

    private async Task<BrowserInstance> LaunchBrowserAsync(bool disableHttp2)
    {
        var args = new List<string> { "--disable-blink-features=AutomationControlled" };
        if (disableHttp2)
            args.Add("--disable-http2");

        var browser = await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chrome",
            Headless = _options.Headless,
            Args = args
        });

        _logger.LogInformation(
            "[BrowserPool] Launched chrome {Version}: headless={Headless}, h2={Http2}",
            browser.Version, _options.Headless, !disableHttp2);

        return new BrowserInstance(browser, browser.Version);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _browsers.Values)
        {
            if (lazy.IsValueCreated)
            {
                try
                {
                    var instance = await lazy.Value;
                    await instance.Browser.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BrowserPool] Failed to close browser instance");
                }
            }
        }
        _browsers.Clear();
        _playwright?.Dispose();
        _semaphore.Dispose();
        _playwrightInitLock.Dispose();
    }

    private sealed record BrowserInstance(IBrowser Browser, string Version);
}
