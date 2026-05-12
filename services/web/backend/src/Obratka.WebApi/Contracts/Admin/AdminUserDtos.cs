namespace Obratka.WebApi.Contracts.Admin;

public sealed record AdminUserListItem(
    Guid Id,
    string Email,
    string FullName,
    bool IsBlocked,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);

public sealed record AdminUserListResponse(int Total, IReadOnlyList<AdminUserListItem> Items);
