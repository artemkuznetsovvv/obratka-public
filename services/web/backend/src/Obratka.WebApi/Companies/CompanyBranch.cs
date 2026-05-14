namespace Obratka.WebApi.Companies;

public class CompanyBranch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string Source { get; set; } = string.Empty;
    // Parser-Service may return empty/null externalId for some sources. Store as "" then;
    // unique index on (CompanyId, Source, ExternalId) is partial — skips empty values.
    public string ExternalId { get; set; } = string.Empty;
    public string? ExternalUrl { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string City { get; set; } = string.Empty;
    public double? Rating { get; set; }
    public int? ReviewCount { get; set; }
    // false = candidate (found by search, not picked by user yet); true = confirmed by user on step 2.
    public bool IsSelected { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
