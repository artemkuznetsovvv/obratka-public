namespace Obratka.WebApi.Contracts.Admin;

public sealed record AdminCompanyListItem(
    Guid Id,
    string Name,
    string? Category,
    string? Subcategory,
    IReadOnlyList<string> Cities,
    int BranchCount,
    int SelectedBranchCount,
    Guid OwnerUserId,
    string OwnerEmail,
    string OwnerFullName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminCompanyListResponse(
    int Total,
    int Limit,
    int Offset,
    IReadOnlyList<AdminCompanyListItem> Items);

public sealed record AdminCompanyBranchDto(
    Guid Id,
    string Source,
    string? ExternalId,
    string? ExternalUrl,
    string Name,
    string? Address,
    string City,
    double? Rating,
    int? ReviewCount,
    bool IsSelected,
    DateTimeOffset CreatedAt);

public sealed record AdminCompanyDetails(
    Guid Id,
    string Name,
    string? Category,
    string? Subcategory,
    IReadOnlyList<string> Cities,
    string? Description,
    Guid OwnerUserId,
    string OwnerEmail,
    string OwnerFullName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> NotificationChatIds,
    IReadOnlyList<AdminCompanyBranchDto> Branches);

// Доп. чаты для дублирования результатов анализов компании (Telegram chat_id: число или @username).
public sealed record UpdateCompanyNotificationChatsRequest(IReadOnlyList<string> ChatIds);
