namespace Obratka.Modules.Notifications;

// Модуль уведомлений знает только userId/monitoringId (примитивы) — а кому и куда слать,
// резолвит Web API (у него доступ к Identity-пользователю и MonitoringConfig). Так модуль
// не зависит от WebApi-типов, а получатель остаётся канало-нейтральным (сейчас Telegram chatId;
// позже сюда добавятся Email/Phone без смены мест вызова — расширяемость по ТЗ).
public interface INotificationRecipientResolver
{
    Task<UserNotificationTarget?> ResolveUserAsync(Guid userId, Guid monitoringId, CancellationToken ct);

    // Telegram chat_id пользователя (или null, если не привязан) — для уведомлений вне мониторинга
    // (напр. «разовый анализ готов»), где нет monitoringId/подписки.
    Task<string?> ResolveChatIdAsync(Guid userId, CancellationToken ct);
}

// Канало-нейтральный получатель + контекст для сборки сообщения.
// ChatId == null → пользователь не привязал Telegram (канал недоступен).
public sealed record UserNotificationTarget(
    string? ChatId,
    bool NotificationsEnabled,
    Guid SeedJobId,
    string CompanyName);
