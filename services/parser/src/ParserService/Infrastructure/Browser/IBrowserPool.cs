using Microsoft.Playwright;
using ParserService.Infrastructure.Proxy;

namespace ParserService.Infrastructure.Browser;

/// <summary>
/// <param name="Proxy">Прокси для контекста (см. ProxyInfo).</param>
/// <param name="DisableHttp2">
/// Запустить отдельный Chromium-инстанс с флагом --disable-http2.
/// Workaround для HTTP-прокси, которые не туннелят h2-фреймы корректно
/// (Google виснет на about:blank через CONNECT-туннель). Используется только
/// в GoogleMapsPlugin. Yandex/2GIS остаются на h2.
/// </param>
/// </summary>
public record BrowserAcquireOptions(ProxyInfo? Proxy = null, bool DisableHttp2 = false);

public interface IBrowserPool
{
    Task<IBrowserContext> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct);
    Task ReleaseAsync(IBrowserContext context);
}
