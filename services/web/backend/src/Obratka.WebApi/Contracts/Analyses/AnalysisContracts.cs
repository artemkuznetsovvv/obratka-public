using System.ComponentModel.DataAnnotations;

namespace Obratka.WebApi.Contracts.Analyses;

// Запрос со стороны фронта на запуск анализа. period — опционально (null = «с самого начала»).
// Источники и выбранные branches не передаём — Web API сам читает их из БД (LogicalBranch.IsSelected
// + CompanyBranch.IsSelected на момент запуска).
public sealed record StartAnalysisRequest(
    [Required] Guid CompanyId,
    DateTimeOffset? PeriodFrom,
    DateTimeOffset? PeriodTo);

public sealed record StartAnalysisResponse(Guid AnalysisJobId);

// Агрегат «сколько отзывов собрано» в разрезе физический филиал × источник для конкретного job-а.
// branchName — joined из webapi_db.LogicalBranches. Если LogicalBranch уже удалён (юзер
// перегруппировал и сохранил), branchName будет null — на фронте показываем placeholder.
public sealed record BranchStatsDto(
    Guid BranchId,
    string? BranchName,
    string? BranchAddress,
    string Source,
    int ReviewCount);
