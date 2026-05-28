namespace Obratka.WebApi.Contracts.Dashboards;

// Шапка дашборда: всё, что нужно, чтобы отрисовать заголовок страницы (название
// компании, перечень филиалов джоба, источники, статус) до подключения карточек
// метрик. Метрики придут отдельными методами/полями по мере реализации.
//
// Period: фактический период джоба в processing_db не хранится (см.
// processing-gateway-todo.md #1). На MVP берём draftPeriodFrom/To из компании
// как best-effort fallback — если юзер изменил настройки после запуска, может
// расходиться с реально проанализированным периодом.
public sealed record DashboardHeaderDto(
    Guid JobId,
    Guid CompanyId,
    string CompanyName,
    IReadOnlyList<DashboardBranchDto> Branches,
    IReadOnlyList<string> Sources,
    string Status,
    int ReviewCount,
    int RecommendationsCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? PeriodFrom,
    DateTimeOffset? PeriodTo);

public sealed record DashboardBranchDto(
    Guid BranchId,
    string? Name,
    string? Address,
    // City берётся из LogicalBranch.City (отдельное поле в webapi_db, а не
    // парсится из Address). Нужно фронту для фильтра «Город» и группировок.
    string? City);
