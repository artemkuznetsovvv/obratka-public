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

            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                Locale = "ru-RU",
                TimezoneId = "Europe/Moscow",
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
            };

            if (options?.Proxy is { } proxy)
            {
                contextOptions.Proxy = new Microsoft.Playwright.Proxy
                {
                    Server = $"http://{proxy.Host}:{proxy.Port}",
                    Username = proxy.Username,
                    Password = proxy.Password
                };
            }

            var context = await _browser!.NewContextAsync(contextOptions);
            _logger.LogDebug("Browser context acquired (proxy: {HasProxy})", options?.Proxy != null);
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

    private async Task EnsureInitializedAsync()
    {
        if (_browser != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_browser != null) return;

            _logger.LogInformation("Initializing Playwright browser pool...");
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome",
                Headless = _options.Headless
            });
            _logger.LogInformation("Playwright browser pool initialized (channel: chrome, headless: {Headless})",
                _options.Headless);
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
