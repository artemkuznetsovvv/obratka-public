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
    /// <summary>
    /// Возвращает следующий прокси в ротации (round-robin).
    /// <paramref name="exclude"/> — список прокси, которые нужно исключить (например, упавшие
    /// в текущем retry-цикле плагина). Сравнение по Host:Port:Username. Если все доступные
    /// прокси находятся в exclude или на cooldown — возвращает null.
    /// </summary>
    Task<ProxyInfo?> GetProxyAsync(
        SourceType source,
        CancellationToken ct,
        IReadOnlyCollection<ProxyInfo>? exclude = null);

    Task ReleaseProxyAsync(ProxyInfo proxy);
    Task ReportFailureAsync(ProxyInfo proxy, ProxyFailureReason reason);
}
