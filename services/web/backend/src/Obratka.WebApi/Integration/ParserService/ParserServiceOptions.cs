namespace Obratka.WebApi.Integration.ParserService;

public sealed class ParserServiceOptions
{
    public const string SectionName = "ParserService";

    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}
