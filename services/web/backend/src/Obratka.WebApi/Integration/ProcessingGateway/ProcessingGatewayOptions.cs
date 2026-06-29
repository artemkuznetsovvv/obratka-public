namespace Obratka.WebApi.Integration.ProcessingGateway;

public sealed class ProcessingGatewayOptions
{
    public const string SectionName = "ProcessingGateway";

    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    // 60s покрывает стрим S3-блобов через прокси (raw/<source>.json может быть несколько МБ).
    public int TimeoutSeconds { get; init; } = 60;
}
