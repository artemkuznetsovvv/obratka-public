namespace Obratka.WebApi.Scheduling;

// Цели Hangfire-jobs мониторинга. TriggerAsync — запуск одного цикла (recurring per-monitoring +
// ручной enqueue); ReconcilePendingAsync — глобальный recurring, финализирует завершившиеся в PG циклы.
public interface IMonitoringCycleRunner
{
    Task TriggerAsync(Guid monitoringId);

    Task ReconcilePendingAsync();
}
