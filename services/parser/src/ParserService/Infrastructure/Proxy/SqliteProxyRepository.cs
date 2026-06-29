using Microsoft.EntityFrameworkCore;
using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Proxy;

public class SqliteProxyRepository : IProxyRepository
{
    private readonly ParserDbContext _db;

    public SqliteProxyRepository(ParserDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProxyEntity>> ListAsync(bool? enabledOnly, CancellationToken ct)
    {
        var q = _db.Proxies.AsNoTracking();
        if (enabledOnly is true) q = q.Where(p => p.Enabled);
        var rows = await q.ToListAsync(ct);
        return rows.OrderBy(p => p.Id).ToList();
    }

    public Task<ProxyEntity?> GetByIdAsync(int id, CancellationToken ct) =>
        _db.Proxies.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<ProxyEntity> AddAsync(ProxyEntity entity, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        _db.Proxies.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        _db.Proxies.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.Enabled = enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetExpiresAtAsync(int id, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.ExpiresAt = expiresAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetHealthAsync(int id, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.FailureCount = 0;
        entity.CooldownUntil = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RecordFailureAsync(int id, DateTimeOffset? cooldownUntil, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.FailureCount += 1;
        if (cooldownUntil.HasValue)
        {
            entity.CooldownUntil = cooldownUntil;
            entity.FailureCount = 0;
        }
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task TouchLastUsedAsync(int id, CancellationToken ct)
    {
        var entity = await _db.Proxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct) => _db.Proxies.CountAsync(ct);
}
