namespace Obratka.WebApi.Integration.ProcessingGateway;

public sealed class ProcessingGatewayOptions
{
    public const string SectionName = "ProcessingGateway";

    public string BaseUrl { get; init; } = string.Empty;
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}
