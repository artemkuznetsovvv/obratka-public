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
}

public class PlaywrightBrowserPool : IBrowserPool, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(3, 3);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<PlaywrightBrowserPool> _logger;
    private readonly BrowserPoolOptions _options;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _realBrowserVersion;

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
    }

    public async Task<IBrowserContext> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);

        try
        {
            await EnsureInitializedAsync();

            var viewport = Viewports[Random.Shared.Next(Viewports.Length)];
            var ua = BuildUserAgent();

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

            var context = await _browser!.NewContextAsync(contextOptions);

            _logger.LogDebug(
                "[BrowserPool] Context создан: UA={UA}, viewport={W}x{H}, proxy={HasProxy}",
                ua[^30..], viewport.Width, viewport.Height, options?.Proxy != null);
            return context;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private string BuildUserAgent()
    {
        // Use real Chrome version from the launched browser to match TLS/JA3 fingerprint
        var version = _realBrowserVersion ?? "136.0.0.0";
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version} Safari/537.36";
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

    private async Task EnsureInitializedAsync()
    {
        if (_browser != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_browser != null) return;

            _logger.LogInformation("[BrowserPool] Инициализация Playwright...");
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome",
                Headless = _options.Headless,
                Args = ["--disable-blink-features=AutomationControlled"]
            });

            // Extract real Chrome version for UA synchronization
            _realBrowserVersion = _browser.Version;

            _logger.LogInformation(
                "[BrowserPool] Инициализирован: chrome {Version}, headless={Headless}",
                _realBrowserVersion, _options.Headless);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _semaphore.Dispose();
        _initLock.Dispose();
    }
}
