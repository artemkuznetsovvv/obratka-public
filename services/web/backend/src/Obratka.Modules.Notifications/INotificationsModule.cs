namespace Obratka.Modules.Notifications;

// Точки уведомлений. Реальная доставка — Telegram (см. NotificationsModule). Когда канал не
// сконфигурирован (нет токена) — модуль работает лог-стабом, не падая. Параметры — примитивы,
// чтобы модуль не зависел от типов Web API; адресата резолвит INotificationRecipientResolver.
public interface INotificationsModule
{
    // Алерт администратору об ошибке (ТЗ §3). Шлётся на все Telegram:AdminChatIds.
    Task SendAdminAlertAsync(AdminAlert alert, CancellationToken ct);

    // Итог цикла мониторинга — пользователю (ТЗ §2: «обновление выполнено» + «+N новых» +
    // «частичное обновление»). status (wire): success|partial|failed|no_new.
    // unavailableSources — человекочитаемые названия недоступных источников (для partial).
    Task SendMonitoringCycleResultAsync(
        Guid userId,
        Guid monitoringId,
        string status,
        int newReviewCount,
        DateTimeOffset? periodFrom,
        DateTimeOffset periodTo,
        IReadOnlyList<string> unavailableSources,
        CancellationToken ct);

    // «Разовый анализ готов» — владельцу + доп. чатам компании (по завершению analysis_job).
    Task SendAnalysisReadyAsync(
        Guid userId,
        Guid companyId,
        Guid jobId,
        string companyName,
        int reviewCount,
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
