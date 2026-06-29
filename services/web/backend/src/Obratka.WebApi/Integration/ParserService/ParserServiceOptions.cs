namespace Obratka.WebApi.Integration.ParserService;

public sealed class ParserServiceOptions
{
    public const string SectionName = "ParserService";

    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    // Search по нескольким источникам с Playwright/прокси/stealth легко перешагивает 30 секунд.
    // Status-poll и proxies-CRUD быстрые — лишний запас на них не вредит.
    public int TimeoutSeconds { get; init; } = 180;
}
