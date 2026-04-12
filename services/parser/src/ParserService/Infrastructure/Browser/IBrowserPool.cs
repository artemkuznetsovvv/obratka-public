using Microsoft.Playwright;
using ParserService.Infrastructure.Proxy;

namespace ParserService.Infrastructure.Browser;

public record BrowserAcquireOptions(ProxyInfo? Proxy = null);

public interface IBrowserPool
{
    Task<IBrowserContext> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct);
    Task ReleaseAsync(IBrowserContext context);
}
