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

// Логический (физический) филиал: Id = branch_id, используемый в анализах/reviews.
public sealed record AdminCompanyLogicalBranchDto(
    Guid Id,
    string Name,
    string? Address,
    string City);

// Последний анализ компании (из PG). Period выводится на фронте из CreatedAt→CompletedAt.
public sealed record AdminCompanyAnalysisDto(
    Guid Id,
    string Status,
    int ReviewCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AdminCompanyMonitoringDto(
    Guid Id,
    string Status,                 // active|paused|error
    IReadOnlyList<string> Sources,
    int WindowDays,
    string Frequency,
    DateTimeOffset? LastCollectedAt,
    string? LastRunStatus,
    bool NotificationsEnabled);

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
    IReadOnlyList<AdminCompanyBranchDto> Branches,
    IReadOnlyList<AdminCompanyLogicalBranchDto> LogicalBranches,
    IReadOnlyList<AdminCompanyAnalysisDto> RecentAnalyses,
    AdminCompanyMonitoringDto? Monitoring);

// Доп. чаты для дублирования результатов анализов компании (Telegram chat_id: число или @username).
public sealed record UpdateCompanyNotificationChatsRequest(IReadOnlyList<string> ChatIds);
