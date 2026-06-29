using Microsoft.Extensions.Options;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

/// <summary>
/// Прокси-ротатор с источником истины в SQLite. Каждый <see cref="GetProxyAsync"/>
/// читает свежий список из БД — кэша нет. Health state (FailureCount / CooldownUntil)
/// персистится в БД. На первом обращении при пустой таблице — seed из конфига.
/// Поддерживаемые протоколы: http, https.
/// </summary>
public class DbProxyRotator : IProxyRotator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxyOptions _options;
    private readonly ILogger<DbProxyRotator> _logger;
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seedChecked;
    private int _index;

    public DbProxyRotator(
        IServiceScopeFactory scopeFactory,
        IOptions<ProxyOptions> options,
        ILogger<DbProxyRotator> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProxyInfo?> GetProxyAsync(
        SourceType source,
        CancellationToken ct,
        IReadOnlyCollection<ProxyInfo>? exclude = null)
    {
        await EnsureSeededAsync(ct);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();

        var entries = await repo.ListAsync(enabledOnly: true, ct);
        if (entries.Count == 0)
        {
            _logger.LogWarning("No enabled proxies in DB — running without proxy for {Source}", source);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var notExpired = entries
            .Where(e => e.ExpiresAt is null || e.ExpiresAt > now)
            .ToList();
        var expiredCount = entries.Count - notExpired.Count;

        var available = notExpired
            .Where(e => e.CooldownUntil is null || e.CooldownUntil <= now)
            .Where(e => exclude is null || !exclude.Any(x => SameProxy(x, e)))
            .ToList();

        if (available.Count == 0)
        {
            if (exclude is { Count: > 0 })
                _logger.LogWarning(
                    "No fresh proxies for {Source}: total={Total}, expired={Expired}, excluded={Excluded}, all others on cooldown",
                    source, entries.Count, expiredCount, exclude.Count);
            else
                _logger.LogWarning(
                    "No usable proxies for {Source}: total={Total}, expired={Expired}, others on cooldown",
                    source, entries.Count, expiredCount);
            return null;
        }

        var idx = Interlocked.Increment(ref _index);
        var picked = available[((idx - 1) % available.Count + available.Count) % available.Count];

        await repo.TouchLastUsedAsync(picked.Id, ct);
        var info = new ProxyInfo(picked.Host, picked.Port, picked.Username, picked.Password, picked.Protocol, picked.Notes);
        _logger.LogDebug("Assigned proxy {Proxy} (id={Id}) for {Source} (pool: {Available}/{Total})",
            info.DisplayName, picked.Id, source, available.Count, entries.Count);
        return info;
    }

    private static bool SameProxy(ProxyInfo a, ProxyEntity b) =>
        a.Host == b.Host && a.Port == b.Port && a.Username == b.Username;

    public Task ReleaseProxyAsync(ProxyInfo proxy)
    {
        _logger.LogDebug("Released proxy {Proxy}", proxy.DisplayName);
        return Task.CompletedTask;
    }

    public async Task ReportFailureAsync(ProxyInfo proxy, ProxyFailureReason reason)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();

        var rows = await repo.ListAsync(enabledOnly: false, CancellationToken.None);
        var entity = rows.FirstOrDefault(e =>
            e.Host == proxy.Host && e.Port == proxy.Port && e.Username == proxy.Username);

        if (entity is null)
        {
            _logger.LogWarning("ReportFailure: proxy {Proxy} not found in DB", proxy.DisplayName);
            return;
        }

        var willCooldown = entity.FailureCount + 1 >= _options.MaxFailuresBeforeCooldown;
        var cooldownUntil = willCooldown
            ? DateTimeOffset.UtcNow.AddSeconds(_options.CooldownSeconds)
            : (DateTimeOffset?)null;

        await repo.RecordFailureAsync(entity.Id, cooldownUntil, CancellationToken.None);

        if (willCooldown)
            _logger.LogWarning(
                "Proxy {Proxy} put on {Cooldown}s cooldown (reason: {Reason})",
                proxy.DisplayName, _options.CooldownSeconds, reason);
        else
            _logger.LogInformation(
                "Proxy {Proxy} failure recorded (reason: {Reason})",
                proxy.DisplayName, reason);
    }

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (_seedChecked) return;
        await _seedLock.WaitAsync(ct);
        try
        {
            if (_seedChecked) return;

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();

            if (await repo.CountAsync(ct) == 0 && _options.Servers.Count > 0)
            {
                _logger.LogInformation("Proxies table empty — seeding {Count} entries from config",
                    _options.Servers.Count);
                foreach (var s in _options.Servers)
                {
                    try
                    {
                        await repo.AddAsync(new ProxyEntity
                        {
                            Host = s.Host,
                            Port = s.Port,
                            Username = s.Username,
                            Password = s.Password,
                            Protocol = NormalizeProtocol(s.Protocol),
                            Enabled = true,
                            Notes = "seeded from config"
                        }, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to seed proxy {Host}:{Port}", s.Host, s.Port);
                    }
                }
            }

            _seedChecked = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    public static string NormalizeProtocol(string? protocol)
    {
        var p = (protocol ?? "http").Trim().ToLowerInvariant();
        return p switch
        {
            "http" or "https" => p,
            _ => throw new InvalidOperationException(
                $"Unsupported proxy protocol '{protocol}'. Allowed: http, https.")
        };
    }
}
