using Microsoft.Extensions.Logging;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public class StubProxyRotator : IProxyRotator
{
    private readonly ILogger<StubProxyRotator> _logger;

    public StubProxyRotator(ILogger<StubProxyRotator> logger)
    {
        _logger = logger;
    }

    public Task<ProxyInfo?> GetProxyAsync(SourceType source, CancellationToken ct)
    {
        _logger.LogWarning("Proxy rotation is not configured. Running without proxy for {Source}", source);
        return Task.FromResult<ProxyInfo?>(null);
    }

    public Task ReleaseProxyAsync(ProxyInfo proxy)
    {
        return Task.CompletedTask;
    }
}
