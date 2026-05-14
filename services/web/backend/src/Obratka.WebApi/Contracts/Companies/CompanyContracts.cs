using System.ComponentModel.DataAnnotations;

namespace Obratka.WebApi.Contracts.Companies;

public sealed record CreateCompanyRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(100)] string? Category,
    [MaxLength(100)] string? Subcategory,
    [Required, MinLength(1)] List<string> Cities,
    [MaxLength(4000)] string? Description);

public sealed record CompanyDto(
    Guid Id,
    string Name,
    string? Category,
    string? Subcategory,
    IReadOnlyList<string> Cities,
    string? Description,
    int BranchCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CompanyBranchDto(
    Guid Id,
    string Source,
    string? ExternalId,
    string? ExternalUrl,
    string Name,
    string? Address,
    string City,
    double? Rating,
    int? ReviewCount);

public sealed record BranchSearchResultItem(
    Guid Id,
    string Source,
    string? ExternalId,
    string? ExternalUrl,
    string Name,
    string? Address,
    double? Rating,
    int? ReviewCount);

public sealed record BranchSearchSourceGroup(
    string Source,
    IReadOnlyList<BranchSearchResultItem> Items);

public sealed record BranchSearchResponse(
    string City,
    IReadOnlyList<BranchSearchSourceGroup> Sources);

public sealed record SaveBranchesRequest(
    [Required, MinLength(1)] List<Guid> BranchIds);
