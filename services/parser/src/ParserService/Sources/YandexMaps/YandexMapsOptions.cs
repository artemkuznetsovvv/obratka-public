namespace ParserService.Sources.YandexMaps;

public class YandexMapsOptions
{
    public const string SectionName = "YandexMaps";

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
}
