using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public interface IProxyRepository
{
    Task<IReadOnlyList<ProxyEntity>> ListAsync(bool? enabledOnly, CancellationToken ct);
    Task<ProxyEntity?> GetByIdAsync(int id, CancellationToken ct);
    Task<ProxyEntity> AddAsync(ProxyEntity entity, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct);
    Task ResetHealthAsync(int id, CancellationToken ct);
    Task RecordFailureAsync(int id, DateTimeOffset? cooldownUntil, CancellationToken ct);
    Task TouchLastUsedAsync(int id, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
