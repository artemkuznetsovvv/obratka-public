namespace ParserService.Sources.TwoGis;

public enum TwoGisCollectionMode
{
    Api,
    BrowserScroll
}

public class TwoGisOptions
{
    public const string SectionName = "TwoGis";

    public TwoGisCollectionMode CollectionMode { get; set; } = TwoGisCollectionMode.Api;

    /// <summary>
    /// Public API key for public-api.reviews.2gis.com.
    /// If empty, extracted automatically from 2gis.ru page.
    /// </summary>
    public string? ReviewApiKey { get; set; }

    public int PageSize { get; set; } = 50;
    public int DelayBetweenPagesMinMs { get; set; } = 1000;
    public int DelayBetweenPagesMaxMs { get; set; } = 3000;
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Maximum scroll iterations in BrowserScroll mode.
    /// </summary>
    public int MaxScrollAttempts { get; set; } = 100;

    /// <summary>
    /// Navigation timeout in milliseconds.
    /// </summary>
    public int NavigationTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Per-request timeout (seconds) for HTTP calls to the 2GIS reviews API.
    /// Applied to the <see cref="System.Net.Http.HttpClient.Timeout"/> of the client used
    /// in <see cref="TwoGisCollectionMode.Api"/> mode. Without it the client falls back to
    /// .NET's default of 100s, which times out on slow proxies / large pages.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// City slug for 2GIS URLs (e.g. "moscow", "spb").
    /// Used to build firm URL: https://2gis.ru/{City}/firm/{firmId}
    /// If empty, uses "moscow" as default.
    /// </summary>
    public string DefaultCity { get; set; } = "moscow";
}
