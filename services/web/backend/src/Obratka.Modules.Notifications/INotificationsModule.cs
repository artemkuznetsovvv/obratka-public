namespace Obratka.Modules.Notifications;

// Точки уведомлений. Реальная доставка в Telegram — зона OBR-39; сейчас лог-стаб
// (см. NotificationsModule). Параметры — примитивы, чтобы модуль не зависел от типов Web API.
public interface INotificationsModule
{
    // Алерт администратору (ошибки пайплайна, частичные/проваленные циклы).
    Task SendAdminAlertAsync(string message, string correlationId, CancellationToken ct);

    // Итог цикла мониторинга — пользователю. status: success|partial|failed.
    Task SendMonitoringCycleResultAsync(
        Guid userId,
        Guid monitoringId,
        string status,
        int newReviewCount,
        DateTimeOffset? periodFrom,
        DateTimeOffset periodTo,
        CancellationToken ct);

    // «Резкий рост негатива» — пользователю (ТЗ §6).
    Task SendNegativeSentimentAlertAsync(
        Guid userId,
        Guid monitoringId,
        double previousNegativePp,
        double currentNegativePp,
        int newReviewCount,
        CancellationToken ct);
}
