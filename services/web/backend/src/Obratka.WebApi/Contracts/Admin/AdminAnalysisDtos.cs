namespace Obratka.WebApi.Contracts.Admin;

public sealed record StartAdminAnalysisRequest(
    Guid CompanyId,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo);

public sealed record RestartSourceAdminRequest(
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo);
