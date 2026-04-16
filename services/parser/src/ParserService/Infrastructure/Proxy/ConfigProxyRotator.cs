using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public class ProxyOptions
{
    public const string SectionName = "Proxy";

    public List<ProxyEntry> Servers { get; set; } = [];

    /// <summary>Number of failures before a proxy is put on cooldown.</summary>
    public int MaxFailuresBeforeCooldown { get; set; } = 3;

    /// <summary>Duration of cooldown in seconds.</summary>
    public int CooldownSeconds { get; set; } = 300;
}

public class ProxyEntry
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Proxy protocol: "http" (default), "https", or "socks5".</summary>
    public string Protocol { get; set; } = "http";
}

public class ConfigProxyRotator : IProxyRotator
{
    private readonly List<ProxyInfo> _proxies;
    private readonly ConcurrentDictionary<string, ProxyHealthState> _health = new();
    private readonly ProxyOptions _proxyOptions;
    private readonly ILogger<ConfigProxyRotator> _logger;
    private int _index;

    public ConfigProxyRotator(IOptions<ProxyOptions> options, ILogger<ConfigProxyRotator> logger)
    {
        _logger = logger;
        _proxyOptions = options.Value;
        _proxies = _proxyOptions.Servers
            .Select(e => new ProxyInfo(e.Host, e.Port, e.Username, e.Password, NormalizeProtocol(e.Protocol)))
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

        // Try to find a healthy proxy, cycling through the full list at most once
        for (var attempt = 0; attempt < _proxies.Count; attempt++)
        {
            var idx = Interlocked.Increment(ref _index);
            var proxy = _proxies[((idx - 1) % _proxies.Count + _proxies.Count) % _proxies.Count];
            var key = ProxyKey(proxy);
            var state = _health.GetOrAdd(key, _ => new ProxyHealthState());

            if (state.IsOnCooldown(_proxyOptions.CooldownSeconds))
            {
                _logger.LogDebug("Proxy {Host}:{Port} is on cooldown, skipping", proxy.Host, proxy.Port);
                continue;
            }

            _logger.LogDebug("Assigned proxy {Host}:{Port} for {Source}", proxy.Host, proxy.Port, source);
            return Task.FromResult<ProxyInfo?>(proxy);
        }

        // All proxies on cooldown — return the least recently failed one
        _logger.LogWarning("All proxies on cooldown — using least recently failed proxy");
        var fallback = _proxies
            .OrderBy(p => _health.TryGetValue(ProxyKey(p), out var s) ? s.CooldownUntil : DateTime.MinValue)
            .First();
        return Task.FromResult<ProxyInfo?>(fallback);
    }

    public Task ReleaseProxyAsync(ProxyInfo proxy)
    {
        _logger.LogDebug("Released proxy {Host}:{Port}", proxy.Host, proxy.Port);
        return Task.CompletedTask;
    }

    public Task ReportFailureAsync(ProxyInfo proxy, ProxyFailureReason reason)
    {
        var key = ProxyKey(proxy);
        var state = _health.GetOrAdd(key, _ => new ProxyHealthState());

        var failures = state.RecordFailure();

        if (failures >= _proxyOptions.MaxFailuresBeforeCooldown)
        {
            state.SetCooldown(_proxyOptions.CooldownSeconds);
            _logger.LogWarning(
                "Proxy {Host}:{Port} put on {Cooldown}s cooldown after {Failures} failures (last: {Reason})",
                proxy.Host, proxy.Port, _proxyOptions.CooldownSeconds, failures, reason);
        }
        else
        {
            _logger.LogInformation(
                "Proxy {Host}:{Port} failure recorded ({Failures}/{Max}): {Reason}",
                proxy.Host, proxy.Port, failures, _proxyOptions.MaxFailuresBeforeCooldown, reason);
        }

        return Task.CompletedTask;
    }

    private static string ProxyKey(ProxyInfo proxy) => $"{proxy.Host}:{proxy.Port}";

    private static string NormalizeProtocol(string? protocol)
    {
        var p = (protocol ?? "http").Trim().ToLowerInvariant();
        return p switch
        {
            "http" or "https" or "socks5" => p,
            "socks" => "socks5",
            _ => throw new InvalidOperationException(
                $"Unsupported proxy protocol '{protocol}'. Allowed: http, https, socks5.")
        };
    }

    private sealed class ProxyHealthState
    {
        private int _failureCount;
        public DateTime CooldownUntil { get; private set; } = DateTime.MinValue;

        public int RecordFailure() => Interlocked.Increment(ref _failureCount);

        public void SetCooldown(int seconds)
        {
            CooldownUntil = DateTime.UtcNow.AddSeconds(seconds);
            Interlocked.Exchange(ref _failureCount, 0);
        }

        public bool IsOnCooldown(int cooldownSeconds)
        {
            if (CooldownUntil <= DateTime.UtcNow) return false;
            return true;
        }
    }
}
