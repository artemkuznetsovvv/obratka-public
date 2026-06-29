using Microsoft.Extensions.Options;

namespace Obratka.WebApi.Integration.ParserService;

internal sealed class ParserApiKeyHandler(
    IOptions<ParserServiceOptions> options,
    ILogger<ParserApiKeyHandler> logger) : DelegatingHandler
{
    private const string HeaderName = "X-Api-Key";
    private readonly ParserServiceOptions _options = options.Value;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            logger.LogWarning(
                "Parser-Service request to {Url} without X-Api-Key — ParserService:ApiKey is empty in config",
                request.RequestUri);
        }
        else if (!request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, _options.ApiKey);
            logger.LogDebug(
                "Injected X-Api-Key for Parser-Service request {Method} {Url}",
                request.Method, request.RequestUri);
        }

        return base.SendAsync(request, ct);
    }
}
