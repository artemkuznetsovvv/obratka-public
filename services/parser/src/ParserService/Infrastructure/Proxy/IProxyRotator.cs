using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public record ProxyInfo(string Host, int Port, string? Username, string? Password, string Protocol = "http")
{
    /// <summary>Full proxy URL, e.g. "http://host:8080" / "https://host:443" / "socks5://host:1080".</summary>
    public string Url => $"{Protocol}://{Host}:{Port}";
}

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
