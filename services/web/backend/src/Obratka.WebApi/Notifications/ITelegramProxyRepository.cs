namespace Obratka.WebApi.Notifications;

// CRUD + health-мутации пула прокси Telegram. Зеркало парсерного IProxyRepository
// (Parser-Service/Infrastructure/Proxy/IProxyRepository.cs), но на webapi_db.
public interface ITelegramProxyRepository
{
    Task<IReadOnlyList<TelegramProxy>> ListAsync(bool? enabledOnly, CancellationToken ct);
    Task<TelegramProxy?> GetByIdAsync(int id, CancellationToken ct);
    Task<TelegramProxy> AddAsync(TelegramProxy entity, CancellationToken ct);
    Task DeleteAsync(int id, CancellationToken ct);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken ct);
    Task SetExpiresAtAsync(int id, DateTimeOffset? expiresAt, CancellationToken ct);
    Task ResetHealthAsync(int id, CancellationToken ct);
    Task RecordFailureAsync(int id, DateTimeOffset? cooldownUntil, CancellationToken ct);
    Task TouchLastUsedAsync(int id, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
