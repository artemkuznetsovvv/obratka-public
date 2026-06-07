using Hangfire;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Scheduling;

// Драйвер уведомлений по разовым анализам. Каждый тик берёт «ещё не уведомлённые» трекеры
// (AnalysisNotification.NotifiedAt == null), спрашивает статус job-а в PG и по терминальному
// статусу шлёт уведомление:
//   completed → «✅ Анализ готов» владельцу (если это не мониторинговый seed-job);
//   partial/failed → admin-алерт об ошибке.
// После отправки проставляет NotifiedAt — больше job не трогаем. Сбой PG/отправки → не помечаем,
// повторим в следующий тик. Зависший job (не терминальный > 24ч) снимаем с наблюдения.
internal sealed class AnalysisNotificationReconciler(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    INotificationsModule notifications,
    ILogger<AnalysisNotificationReconciler> logger) : IAnalysisNotificationReconciler
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ReconcileAsync()
    {
        var pending = await db.AnalysisNotifications
            .Where(a => a.NotifiedAt == null)
            .OrderBy(a => a.CreatedAt)
            .Take(50)
            .ToListAsync(Ct);
        if (pending.Count == 0) return;

        foreach (var watch in pending)
        {
            AnalysisJobDto? job;
            try
            {
                job = await gateway.GetAnalysisAsync(watch.JobId, Ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis-notify: PG GetAnalysis failed for {JobId} — retry later", watch.JobId);
                continue;
            }

            if (job is null)
            {
                // Job исчез из PG — снимаем с наблюдения.
                watch.NotifiedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(Ct);
                continue;
            }

            if (!IsTerminal(job.Status))
            {
                // Защита от вечного наблюдения за зависшим job-ом.
                if (watch.CreatedAt < DateTimeOffset.UtcNow.AddHours(-24))
                {
                    logger.LogWarning("Analysis-notify: job {JobId} не достиг терминального статуса за 24ч — снимаем", watch.JobId);
                    watch.NotifiedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(Ct);
                }
                continue;
            }

            try
            {
                if (job.Status == "completed")
                {
                    // Мониторинговый seed-job уведомляет цикл мониторинга — здесь молчим.
                    var isMonitored = await db.MonitoringConfigs.AsNoTracking()
                        .AnyAsync(m => m.SeedJobId == job.Id, Ct);
                    if (!isMonitored)
                        await notifications.SendAnalysisReadyAsync(
                            watch.UserId, job.CompanyId, job.Id, await CompanyNameAsync(job.CompanyId), job.ReviewCount, Ct);
                }
                else // partial | failed → admin-алерт (ТЗ §3)
                {
                    var unavailable = job.CollectionProgress
                        .Where(kv => !string.Equals(kv.Value.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Value.Error is { Length: > 0 } e
                            ? $"{BranchSources.Label(kv.Key)} ({e})"
                            : BranchSources.Label(kv.Key))
                        .ToList();
                    var reason = job.Error
                        ?? (unavailable.Count > 0
                            ? $"Источники с ошибкой: {string.Join("; ", unavailable)}"
                            : $"Анализ завершился со статусом {job.Status}");
                    await notifications.SendAdminAlertAsync(new AdminAlert(
                        Stage: job.Status == "failed" ? "Анализ" : "Сбор",
                        Reason: reason,
                        Severity: job.Status == "failed" ? "critical" : "warning",
                        EventId: Guid.NewGuid().ToString("N"),
                        UserId: watch.UserId,
                        CompanyId: job.CompanyId,
                        CompanyName: await CompanyNameAsync(job.CompanyId),
                        JobId: job.Id), Ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis-notify: send failed for {JobId} — retry later", watch.JobId);
                continue; // не помечаем NotifiedAt → повторим
            }

            watch.NotifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
            logger.LogInformation("Analysis-notify: job {JobId} ({Status}) — уведомление отправлено", job.Id, job.Status);
        }
    }

    private async Task<string> CompanyNameAsync(Guid companyId)
        => await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(Ct) ?? "—";

    private static bool IsTerminal(string status) => status is "completed" or "partial" or "failed";
}
