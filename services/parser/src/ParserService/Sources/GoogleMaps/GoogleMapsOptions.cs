namespace ParserService.Sources.GoogleMaps;

public enum GoogleMapsCollectionMode
{
    /// <summary>
    /// Scroll reviews in browser, parse review cards from DOM.
    /// Simplest approach. Data: relative dates only, translated text, no authorId.
    /// </summary>
    BrowserScroll,

    /// <summary>
    /// Scroll reviews in browser, intercept listugcposts API responses.
    /// Best of both worlds: real browser behavior + full API data
    /// (exact timestamps, original text, authorId, language).
    /// </summary>
    HybridScroll
}

public class GoogleMapsOptions
{
    public const string SectionName = "GoogleMaps";

    public GoogleMapsCollectionMode CollectionMode { get; set; } = GoogleMapsCollectionMode.HybridScroll;

    /// <summary>
    /// Max reviews per API response page (applies to HybridScroll intercept).
    /// Google Maps supports up to 20.
    /// </summary>
    public int PageSize { get; set; } = 10;

    public int DelayBetweenPagesMinMs { get; set; } = 2000;
    public int DelayBetweenPagesMaxMs { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Maximum scroll iterations before stopping.
    /// </summary>
    public int MaxScrollAttempts { get; set; } = 150;

    public int NavigationTimeoutMs { get; set; } = 60_000;
}
