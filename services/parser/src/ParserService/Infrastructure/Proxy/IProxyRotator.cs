using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public record ProxyInfo(string Host, int Port, string? Username, string? Password);

public interface IProxyRotator
{
    Task<ProxyInfo?> GetProxyAsync(SourceType source, CancellationToken ct);
    Task ReleaseProxyAsync(ProxyInfo proxy);
}
