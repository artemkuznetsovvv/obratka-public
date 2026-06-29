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
    // Опц. координаты карточки — пока null (парсер не отдаёт). Заложено под ADR-следующее.
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    // false = candidate (found by search, not picked by user yet) OR provider explicitly disabled
    // inside its logical branch on step 2. true = enabled within its LogicalBranch.
    public bool IsSelected { get; set; }
    // Привязка к логическому филиалу. null = карточка ещё не сгруппирована (unmatched)
    // или помечена «Игнорировать» пользователем.
    public Guid? LogicalBranchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
