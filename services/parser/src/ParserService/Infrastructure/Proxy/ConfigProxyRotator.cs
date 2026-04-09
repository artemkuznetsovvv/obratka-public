using Microsoft.Extensions.Options;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public class ProxyOptions
{
    public const string SectionName = "Proxy";

    public List<ProxyEntry> Servers { get; set; } = [];
}

public class ProxyEntry
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class ConfigProxyRotator : IProxyRotator
{
    private readonly List<ProxyInfo> _proxies;
    private readonly ILogger<ConfigProxyRotator> _logger;
    private int _index;

    public ConfigProxyRotator(IOptions<ProxyOptions> options, ILogger<ConfigProxyRotator> logger)
    {
        _logger = logger;
        _proxies = options.Value.Servers
            .Select(e => new ProxyInfo(e.Host, e.Port, e.Username, e.Password))
            .ToList();

        if (_proxies.Count == 0)
            _logger.LogWarning("No proxies configured in Proxy:Servers — running without proxy");
        else
            _logger.LogInformation("Loaded {Count} proxies from configuration", _proxies.Count);
    }

    public Task<ProxyInfo?> GetProxyAsync(SourceType source, CancellationToken ct)
    {
        if (_proxies.Count == 0)
            return Task.FromResult<ProxyInfo?>(null);

        var idx = Interlocked.Increment(ref _index);
        var proxy = _proxies[((idx - 1) % _proxies.Count + _proxies.Count) % _proxies.Count];

        _logger.LogDebug("Assigned proxy {Host}:{Port} for {Source}", proxy.Host, proxy.Port, source);
        return Task.FromResult<ProxyInfo?>(proxy);
    }

    public Task ReleaseProxyAsync(ProxyInfo proxy)
    {
        _logger.LogDebug("Released proxy {Host}:{Port}", proxy.Host, proxy.Port);
        return Task.CompletedTask;
    }
}
