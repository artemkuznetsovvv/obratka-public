namespace Obratka.Modules.Notifications;

// Модуль уведомлений знает только userId/monitoringId/companyId (примитивы) — а кому и куда слать,
// резолвит Web API (у него доступ к Identity-пользователю, MonitoringConfig и Company). Так модуль
// не зависит от WebApi-типов, а получатель остаётся канало-нейтральным (сейчас Telegram chatId;
// позже сюда добавятся Email/Phone без смены мест вызова — расширяемость по ТЗ).
public interface INotificationRecipientResolver
{
    Task<UserNotificationTarget?> ResolveUserAsync(Guid userId, Guid monitoringId, CancellationToken ct);

    // Получатели уведомления по разовому анализу (вне мониторинга): чат владельца + доп. чаты компании.
    Task<AnalysisRecipients> ResolveAnalysisRecipientsAsync(Guid userId, Guid companyId, CancellationToken ct);
}

// Канало-нейтральный получатель + контекст для сборки сообщения по мониторингу.
// ChatId == null → владелец не привязал Telegram. ExtraChatIds — доп. чаты компании (админ-настройка),
// получают результаты независимо от персональной подписки владельца (NotificationsEnabled).
public sealed record UserNotificationTarget(
    string? ChatId,
    bool NotificationsEnabled,
    Guid SeedJobId,
    string CompanyName,
    IReadOnlyList<string> ExtraChatIds);

// Получатели по разовому анализу: личный чат владельца (или null) + доп. чаты компании.
public sealed record AnalysisRecipients(
    string? OwnerChatId,
    IReadOnlyList<string> ExtraChatIds);
