namespace Obratka.WebApi.Contracts.Admin;

public sealed record AdminUserListItem(
    Guid Id,
    string Email,
    string FullName,
    bool IsBlocked,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    int CompaniesCount,
    DateTimeOffset? LastActivityAt);

public sealed record AdminUserListResponse(int Total, IReadOnlyList<AdminUserListItem> Items);

// Компания пользователя в карточке: счётчик анализов берётся из PG (null/«—» при недоступности).
public sealed record AdminUserCompanyDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Sources,
    int BranchCount,
    int? AnalysesCount,
    bool HasActiveMonitoring);

public sealed record AdminUserDetails(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    DateTimeOffset CreatedAt,
    bool IsBlocked,
    DateTimeOffset? LastActivityAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<AdminUserCompanyDto> Companies);

// Редактирование пользователя админом (пароль здесь НЕ меняется — отдельным set-password).
public sealed record AdminUpdateUserRequest(string Email, string FullName, string? PhoneNumber);
