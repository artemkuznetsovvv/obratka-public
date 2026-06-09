using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Notifications;

// Зеркало парсерного SqliteProxyRepository, но на WebApiDbContext.TelegramProxies (Postgres).
public sealed class TelegramProxyRepository(WebApiDbContext db) : ITelegramProxyRepository
{
    public async Task<IReadOnlyList<TelegramProxy>> ListAsync(bool? enabledOnly, CancellationToken ct)
    {
        var q = db.TelegramProxies.AsNoTracking();
        if (enabledOnly is true) q = q.Where(p => p.Enabled);
        var rows = await q.ToListAsync(ct);
        return rows.OrderBy(p => p.Id).ToList();
    }

    public Task<TelegramProxy?> GetByIdAsync(int id, CancellationToken ct) =>
        db.TelegramProxies.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<TelegramProxy> AddAsync(TelegramProxy entity, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        db.TelegramProxies.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        db.TelegramProxies.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.Enabled = enabled;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetExpiresAtAsync(int id, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.ExpiresAt = expiresAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetHealthAsync(int id, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.FailureCount = 0;
        entity.CooldownUntil = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordFailureAsync(int id, DateTimeOffset? cooldownUntil, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.FailureCount += 1;
        if (cooldownUntil.HasValue)
        {
            entity.CooldownUntil = cooldownUntil;
            entity.FailureCount = 0;
        }
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task TouchLastUsedAsync(int id, CancellationToken ct)
    {
        var entity = await db.TelegramProxies.FindAsync([id], ct);
        if (entity is null) return;
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct) => db.TelegramProxies.CountAsync(ct);
}
