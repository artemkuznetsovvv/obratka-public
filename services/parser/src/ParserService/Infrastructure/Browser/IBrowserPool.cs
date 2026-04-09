using ParserService.Infrastructure.Proxy;

namespace ParserService.Infrastructure.Browser;

public record BrowserAcquireOptions(ProxyInfo? Proxy = null);

public interface IBrowserPool
{
    Task<object> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct);
    Task ReleaseAsync(object context);
}
