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
