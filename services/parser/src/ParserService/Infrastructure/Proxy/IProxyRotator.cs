using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public record ProxyInfo(
    string Host,
    int Port,
    string? Username,
    string? Password,
    string Protocol = "http",
    string? Notes = null)
{
    /// <summary>Full proxy URL, e.g. "http://host:8080" / "https://host:443".</summary>
    public string Url => $"{Protocol}://{Host}:{Port}";

    /// <summary>For logs: "Notes (Host:Port)" if Notes set, otherwise "Host:Port".</summary>
    public string DisplayName => string.IsNullOrEmpty(Notes)
        ? $"{Host}:{Port}"
        : $"{Notes} ({Host}:{Port})";
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
