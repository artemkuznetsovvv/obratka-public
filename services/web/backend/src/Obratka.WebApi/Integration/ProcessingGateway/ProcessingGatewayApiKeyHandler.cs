using Microsoft.Extensions.Options;

namespace Obratka.WebApi.Integration.ProcessingGateway;

internal sealed class ProcessingGatewayApiKeyHandler(
    IOptions<ProcessingGatewayOptions> options,
    ILogger<ProcessingGatewayApiKeyHandler> logger) : DelegatingHandler
{
    private const string HeaderName = "X-Api-Key";
    private readonly ProcessingGatewayOptions _options = options.Value;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            logger.LogWarning(
                "Processing-Gateway request to {Url} without X-Api-Key — ProcessingGateway:ApiKey is empty in config",
                request.RequestUri);
        }
        else if (!request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, _options.ApiKey);
            logger.LogDebug(
                "Injected X-Api-Key for Processing-Gateway request {Method} {Url}",
                request.Method, request.RequestUri);
        }

        return base.SendAsync(request, ct);
    }
}
