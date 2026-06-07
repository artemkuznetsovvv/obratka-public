using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Notifications;

// Реализация резолвера получателя (модуль уведомлений объявляет интерфейс, WebApi его наполняет —
// у него доступ к Identity-пользователю + MonitoringConfig). Возвращает chatId (или null, если
// Telegram не привязан), флаг подписки, seedJobId (для ссылки на дашборд) и имя компании.
internal sealed class WebApiNotificationRecipientResolver(WebApiDbContext db) : INotificationRecipientResolver
{
    public async Task<UserNotificationTarget?> ResolveUserAsync(
        Guid userId, Guid monitoringId, CancellationToken ct)
    {
        var cfg = await db.MonitoringConfigs.AsNoTracking()
            .Where(m => m.Id == monitoringId)
            .Select(m => new { m.UserId, m.CompanyId, m.SeedJobId, m.NotificationsEnabled })
            .FirstOrDefaultAsync(ct);
        if (cfg is null) return null;

        var chatId = await db.Users.AsNoTracking()
            .Where(u => u.Id == cfg.UserId)
            .Select(u => u.TelegramChatId)
            .FirstOrDefaultAsync(ct);

        var companyName = await db.Companies.AsNoTracking()
            .Where(c => c.Id == cfg.CompanyId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct) ?? "—";

        return new UserNotificationTarget(chatId, cfg.NotificationsEnabled, cfg.SeedJobId, companyName);
    }
}
