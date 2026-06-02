using Microsoft.Extensions.Logging;

namespace Obratka.Modules.Notifications;

// Лог-стаб: фиксирует, что уведомление БЫ ушло. Реальную доставку в Telegram реализует OBR-39
// (ApplicationUser.TelegramChatId уже есть). До тех пор цикл мониторинга не падает на отправке.
internal sealed class NotificationsModule(ILogger<NotificationsModule> logger) : INotificationsModule
{
    public Task SendAdminAlertAsync(string message, string correlationId, CancellationToken ct)
    {
        logger.LogInformation("[notify:admin] {Message} (corr={CorrelationId})", message, correlationId);
        return Task.CompletedTask;
    }

    public Task SendMonitoringCycleResultAsync(
        Guid userId, Guid monitoringId, string status, int newReviewCount,
        DateTimeOffset? periodFrom, DateTimeOffset periodTo, CancellationToken ct)
    {
        logger.LogInformation(
            "[notify:user] monitoring cycle result: user={UserId} monitoring={MonitoringId} status={Status} " +
            "new={NewReviewCount} period={PeriodFrom}..{PeriodTo}",
            userId, monitoringId, status, newReviewCount, periodFrom, periodTo);
        return Task.CompletedTask;
    }

    public Task SendNegativeSentimentAlertAsync(
        Guid userId, Guid monitoringId, double previousNegativePp, double currentNegativePp,
        int newReviewCount, CancellationToken ct)
    {
        logger.LogWarning(
            "[notify:user] NEGATIVE SPIKE: user={UserId} monitoring={MonitoringId} " +
            "{Prev}pp -> {Cur}pp over {NewReviewCount} new reviews",
            userId, monitoringId, previousNegativePp, currentNegativePp, newReviewCount);
        return Task.CompletedTask;
    }
}
