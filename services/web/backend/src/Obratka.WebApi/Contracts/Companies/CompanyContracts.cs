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
    // ReviewCount = «число оценок» (rating votes) — что источник показывает рядом
    // с рейтингом. Для Google это совпадает с реальным числом отзывов.
    int? ReviewCount,
    // Настоящее число отзывов с текстом, если удалось точно достать. null когда
    // источник его не отдаёт (Yandex multi-result list). Search-only поле — в
    // CompanyBranch не сохраняется.
    int? RealReviewsCount);

public sealed record BranchSearchSourceGroup(
    string Source,
    IReadOnlyList<BranchSearchResultItem> Items);

public sealed record BranchSearchResponse(
    string City,
    IReadOnlyList<BranchSearchSourceGroup> Sources,
    // Автогруппировка карточек разных источников в физические филиалы.
    // GroupKey — временный id, нестабильный между вызовами; фронт привязывает
    // выбор юзера к нему до сохранения.
    IReadOnlyList<LogicalGroupDto> LogicalGroups,
    // Карточки, которые автоматика не смогла привязать ни к одной группе (singleton
    // компонент в Union-Find). Юзер на UI вручную привязывает их к группам или игнорит.
    IReadOnlyList<BranchSearchResultItem> Unmatched);

public sealed record LogicalGroupDto(
    string GroupKey,
    string CanonicalName,
    string CanonicalAddress,
    string City,
    int MatchScore,
    IReadOnlyList<BranchSearchResultItem> Providers);

public sealed record SaveBranchesRequest(
    [Required, MinLength(1)] List<Guid> BranchIds);

// Запрос для нового step-2 submit: пересохраняет группировку компании целиком.
// Старые LogicalBranch'ы компании удаляются (cards автоматически отвязываются через SetNull),
// взамен создаются из запроса.
public sealed record SaveBranchGroupsRequest(
    [Required] List<SaveBranchGroup> Groups,
    [Required] List<Guid> IgnoredBranchIds);

public sealed record SaveBranchGroup(
    // Name/Address могут быть пустыми если карточка-источник пришла без этих полей
    // (Google иногда отдаёт null address). AllowEmptyStrings=true чтобы DataAnnotations
    // не зарезали 400-кой — в БД сохранится пустая строка, NOT NULL не нарушаем.
    [Required(AllowEmptyStrings = true), MaxLength(500)] string Name,
    [Required(AllowEmptyStrings = true), MaxLength(1024)] string Address,
    [Required, MaxLength(200)] string City,
    bool IsSelected,
    [Required] List<SaveBranchGroupProvider> Providers);

public sealed record SaveBranchGroupProvider(
    [Required] Guid BranchId,
    bool IsEnabled);

// Ответ GET-эндпоинта для step 3 + повторных входов в воронку.
public sealed record LogicalBranchDto(
    Guid Id,
    string Name,
    string Address,
    string City,
    bool IsSelected,
    IReadOnlyList<LogicalBranchProviderDto> Providers);

public sealed record LogicalBranchProviderDto(
    Guid BranchId,
    string Source,
    string? ExternalId,
    string? ExternalUrl,
    string Name,
    string? Address,
    double? Rating,
    int? ReviewCount,
    bool IsEnabled);
