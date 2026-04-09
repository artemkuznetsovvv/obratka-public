namespace ParserService.Sources.YandexMaps;

public enum YandexCollectionMode
{
    Api,
    BrowserScroll
}

public class YandexMapsOptions
{
    public const string SectionName = "YandexMaps";

    public YandexCollectionMode CollectionMode { get; set; } = YandexCollectionMode.BrowserScroll;

    public int PageSize { get; set; } = 50;
    public int MaxPages { get; set; } = 12;
    public int DelayBetweenPagesMinMs { get; set; } = 2000;
    public int DelayBetweenPagesMaxMs { get; set; } = 5000;
    public int DelayBetweenOrgsMinMs { get; set; } = 10000;
    public int DelayBetweenOrgsMaxMs { get; set; } = 30000;
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// API key for external captcha-solving service (2Captcha).
    /// If empty, only automatic checkbox click is attempted.
    /// </summary>
    public string? CaptchaSolverApiKey { get; set; }

    /// <summary>
    /// Whether to "warm up" the session by visiting yandex.ru first.
    /// Reduces the chance of triggering SmartCaptcha.
    /// </summary>
    public bool WarmUpSession { get; set; } = true;

    /// <summary>
    /// Maximum scroll/click iterations in BrowserScroll mode.
    /// </summary>
    public int MaxScrollAttempts { get; set; } = 50;

    /// <summary>
    /// Navigation timeout in milliseconds for page.GotoAsync.
    /// Increase for slow proxies or debugging (e.g. 180000 = 3 min).
    /// </summary>
    public int NavigationTimeoutMs { get; set; } = 60_000;
}
