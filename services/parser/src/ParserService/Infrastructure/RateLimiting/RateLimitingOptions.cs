namespace ParserService.Infrastructure.RateLimiting;

public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Per-source throttling configuration.
    /// Key = source slug ("yandex", "2gis", "google").
    /// </summary>
    public Dictionary<string, SourceRateLimitOptions> Sources { get; set; } = new();
}

public class SourceRateLimitOptions
{
    /// <summary>
    /// Max organizations being processed concurrently for this source.
    /// </summary>
    public int MaxConcurrentOrgs { get; set; } = 1;

    /// <summary>
    /// Max organizations processed per hour (sliding window).
    /// 0 = unlimited.
    /// </summary>
    public int MaxOrgsPerHour { get; set; } = 15;

    /// <summary>
    /// Minimum delay (ms) after finishing one organization before starting the next.
    /// </summary>
    public int DelayAfterOrgMinMs { get; set; } = 10_000;

    /// <summary>
    /// Maximum delay (ms) after finishing one organization before starting the next.
    /// </summary>
    public int DelayAfterOrgMaxMs { get; set; } = 30_000;
}
