namespace Obratka.WebApi.Companies;

// Snapshot of a Parser-Service search response, keyed by (query, city, source).
// Reused across users/companies; expires_at drives invalidation.
public class SearchCacheEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string QueryNormalized { get; set; } = string.Empty;
    public string CityNormalized { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<SearchCacheItem> Results { get; set; } = new();
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SearchCacheItem
{
    public string ExternalId { get; set; } = string.Empty;
    public string ExternalUrl { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public double? Rating { get; set; }
    public int? ReviewCount { get; set; }
}
