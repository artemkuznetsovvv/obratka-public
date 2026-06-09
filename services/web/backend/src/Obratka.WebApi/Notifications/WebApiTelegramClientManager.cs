using Microsoft.Extensions.Options;
using Obratka.Modules.Notifications;
using Telegram.Bot;

namespace Obratka.WebApi.Notifications;

// Реализация ITelegramClientManager (singleton): владеет активным ITelegramBotClient и ротирует
// прокси из пула webapi_db. DbContext/репозиторий — scoped, поэтому каждое обращение к БД идёт
// через свежий scope (IServiceScopeFactory), как DbProxyRotator в Parser. Выбор прокси —
// тот же предикат, что в Parser: enabled ∧ (¬expired) ∧ (¬cooldown) ∧ (≠ исключённый), round-robin.
//
// Fallback (back-compat/dev): если в пуле нет НИ ОДНОГО enabled-прокси — строим клиент по строке
// Telegram:Proxy (или прямое соединение). Если enabled-прокси есть, но все в cooldown/просрочены —
// клиента нет (Current=null): receiver подождёт NoProxyRetryDelaySeconds и попробует снова.
public sealed class WebApiTelegramClientManager(
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> telegram,
    IOptions<TelegramProxyOptions> proxyOptions,
    ILogger<WebApiTelegramClientManager> logger) : ITelegramClientManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITelegramBotClient? _current;
    private int? _currentProxyId;
    private int _index;

    // Снимок активного клиента без блокировки. Ссылка читается/пишется атомарно; Volatile — для
    // видимости между потоками (receiver пишет в RebuildAsync, sender'ы читают из request-потоков).
    // Устаревший снимок безопасен: это валидный клиент (старые НЕ диспозим — см. RebuildAsync),
    // просто на прежнем прокси; следующий send возьмёт свежий.
    public ITelegramBotClient? Current => Volatile.Read(ref _current);

    public Task<bool> EnsureCurrentAsync(CancellationToken ct)
        => Volatile.Read(ref _current) is not null ? Task.FromResult(true) : RebuildAsync(excludeProxyId: null, ct);

    public async Task<bool> RotateAsync(string reason, CancellationToken ct)
    {
        var failedId = _currentProxyId;
        logger.LogWarning("Telegram: ротация прокси (причина: {Reason}), текущий id={ProxyId}", reason, failedId);

        if (failedId is { } id)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<ITelegramProxyRepository>();
                // Порог провалов как у Parser (по умолчанию 1 → cooldown сразу). Cooldown ставим
                // только при достижении порога — тогда пикер перестанет выбирать этот прокси.
                var entity = await repo.GetByIdAsync(id, ct);
                var willCooldown = (entity?.FailureCount ?? 0) + 1 >= proxyOptions.Value.MaxFailuresBeforeCooldown;
                var cooldownUntil = willCooldown
                    ? DateTimeOffset.UtcNow.AddSeconds(proxyOptions.Value.CooldownSeconds)
                    : (DateTimeOffset?)null;
                await repo.RecordFailureAsync(id, cooldownUntil, ct);
                if (willCooldown)
                    logger.LogWarning("Telegram-прокси id={ProxyId} → cooldown {Sec}s", id, proxyOptions.Value.CooldownSeconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram: не удалось записать сбой прокси id={ProxyId}", id);
            }
        }

        return await RebuildAsync(excludeProxyId: failedId, ct);
    }

    private async Task<bool> RebuildAsync(int? excludeProxyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Старый _current НЕ диспозим намеренно: in-flight send мог захватить ссылку (был бы
            // ObjectDisposedException). Ротации cooldown-gated (редкие) → старый HttpClient освобождает
            // GC (у SocketsHttpHandler есть финализатор) — настоящей утечки/исчерпания сокетов нет.
            var token = telegram.Value.BotToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                Volatile.Write(ref _current, null);
                _currentProxyId = null;
                return false;
            }

            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITelegramProxyRepository>();
            var enabled = await repo.ListAsync(enabledOnly: true, ct);
            var now = DateTimeOffset.UtcNow;

            var usable = enabled
                .Where(p => p.ExpiresAt is null || p.ExpiresAt > now)
                .Where(p => p.CooldownUntil is null || p.CooldownUntil <= now)
                .Where(p => excludeProxyId is null || p.Id != excludeProxyId)
                .ToList();

            if (usable.Count > 0)
            {
                var idx = Interlocked.Increment(ref _index);
                var picked = usable[((idx - 1) % usable.Count + usable.Count) % usable.Count];
                await repo.TouchLastUsedAsync(picked.Id, ct);
                Volatile.Write(ref _current, TelegramProxyClientFactory.Build(
                    token, picked.Protocol, picked.Host, picked.Port, picked.Username, picked.Password));
                _currentProxyId = picked.Id;
                logger.LogInformation(
                    "Telegram-прокси выбран: id={ProxyId} {Protocol}://{Host}:{Port}",
                    picked.Id, picked.Protocol, picked.Host, picked.Port);
                return true;
            }

            // Fallback ТОЛЬКО при полностью пустом пуле включённых прокси (пул не настроен / dev) →
            // строка конфига Telegram:Proxy или прямое соединение. Если же включённые прокси ЕСТЬ,
            // но все в cooldown/просрочены — НЕ падаем в direct (на блокирующем хостинге он бесполезен),
            // а ждём: cooldown истечёт → прокси вернётся в ротацию (self-heal), receiver делает backoff.
            if (enabled.Count == 0)
            {
                var fallback = telegram.Value.Proxy;
                Volatile.Write(ref _current, TelegramProxyClientFactory.Build(token, fallback));
                _currentProxyId = null;
                logger.LogInformation("Telegram-прокси: пул пуст, fallback={Mode}",
                    string.IsNullOrWhiteSpace(fallback) ? "direct" : "config-proxy");
                return true;
            }

            // Прокси есть, но все в cooldown/просрочены → ждём (клиента нет).
            Volatile.Write(ref _current, null);
            _currentProxyId = null;
            logger.LogWarning(
                "Telegram-прокси: нет пригодных (все в cooldown/просрочены), enabled={Count}", enabled.Count);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
