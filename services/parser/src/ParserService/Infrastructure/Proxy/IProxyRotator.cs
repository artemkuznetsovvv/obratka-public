using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public record ProxyInfo(string Host, int Port, string? Username, string? Password);

public enum ProxyFailureReason
{
    SmartCaptcha,
    CsrfError,
    Timeout,
    ConnectionError,
    ServerError
}

public interface IProxyRotator
{
    Task<ProxyInfo?> GetProxyAsync(SourceType source, CancellationToken ct);
    Task ReleaseProxyAsync(ProxyInfo proxy);
    Task ReportFailureAsync(ProxyInfo proxy, ProxyFailureReason reason);
}
