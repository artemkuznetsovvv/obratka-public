using Hangfire;
using Obratka.WebApi.Monitoring;

namespace Obratka.WebApi.Scheduling;

// Тонкая обёртка над Hangfire: регистрация/снятие recurring-job на мониторинг,
// ручной enqueue («Обновить вручную»), и глобальный reconcile-job.
public interface IMonitoringScheduler
{
    void Register(MonitoringConfig config);
    void Remove(Guid monitoringId);
    string EnqueueNow(Guid monitoringId);
    void EnsureReconcileJob();
}

internal sealed class MonitoringScheduler(
    IRecurringJobManager recurring,
    IBackgroundJobClient background) : IMonitoringScheduler
{
    private static readonly RecurringJobOptions Utc = new() { TimeZone = TimeZoneInfo.Utc };

    public const string ReconcileJobId = "monitoring-reconcile";
    // Реконсайл — раз в 2 минуты (циклы асинхронные: сбор+LLM минуты).
    private const string ReconcileCron = "*/2 * * * *";

    public void Register(MonitoringConfig config)
        => recurring.AddOrUpdate<IMonitoringCycleRunner>(
            RecurringId(config.Id),
            r => r.TriggerAsync(config.Id),
            config.CronSchedule,
            Utc);

    public void Remove(Guid monitoringId)
        => recurring.RemoveIfExists(RecurringId(monitoringId));

    public string EnqueueNow(Guid monitoringId)
        => background.Enqueue<IMonitoringCycleRunner>(r => r.TriggerAsync(monitoringId));

    public void EnsureReconcileJob()
        => recurring.AddOrUpdate<IMonitoringCycleRunner>(
            ReconcileJobId,
            r => r.ReconcilePendingAsync(),
            ReconcileCron,
            Utc);

    private static string RecurringId(Guid monitoringId) => $"monitoring-{monitoringId:N}";
}
