namespace Obratka.WebApi.Monitoring;

// ----- Requests -----

public sealed record CreateMonitoringRequest(
    Guid CompanyId,
    Guid SeedJobId,
    List<string> Sources,
    List<Guid> BranchIds,
    string Frequency); // enum-токен: Every10Min|Every30Min|Daily|Weekly|Biweekly|Monthly

public sealed record UpdateMonitoringRequest(
    List<string> Sources,
    List<Guid> BranchIds,
    string Frequency);

public sealed record SetNotificationsRequest(bool Enabled);

// ----- Responses -----

public sealed record MonitoringBranchDto(Guid Id, string? Name, string? Address, string? City);

public sealed record MonitoringListItemDto(
    Guid Id,
    Guid CompanyId,
    string CompanyName,
    Guid SeedJobId,
    IReadOnlyList<string> Sources,
    IReadOnlyList<MonitoringBranchDto> Branches,
    int WindowDays,
    string Frequency,
    string Status,            // active|paused|error
    DateTimeOffset? LastCollectedAt,
    string? LastRunStatus,    // success|partial|failed|null
    bool NotificationsEnabled,
    DateTimeOffset CreatedAt);

public sealed record MonitoringCycleDto(
    int CycleNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,            // running|success|partial|failed
    DateTimeOffset? PeriodFrom,
    DateTimeOffset PeriodTo,
    int NewReviewCount,
    int TotalReviewsAtCycle,
    double NegativeRatioPp,
    bool NegativeSpikeTriggered,
    string? Summary,
    IReadOnlyList<RecommendationSnapshotDto> Recommendations,
    string? Error);

public sealed record RecommendationSnapshotDto(
    int Priority,
    string Topic,
    string Title,
    string Body,
    string? ExpectedImpact,
    IReadOnlyList<string> Evidence);

public sealed record MonitoringDetailDto(
    MonitoringListItemDto Monitoring,
    IReadOnlyList<MonitoringCycleDto> Cycles);

public sealed record CreateMonitoringResponse(Guid Id);

public sealed record MonitoringListResponse(IReadOnlyList<MonitoringListItemDto> Items);
