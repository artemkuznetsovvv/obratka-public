using Obratka.WebApi.Monitoring;

namespace Obratka.WebApi.Scheduling;

// Частоты → cron (UTC) + наборы, доступные по роли.
// Отклонение от ТЗ §1 (час/6ч/12ч/сутки) — согласовано: Admin получает тестовые частые
// интервалы, обычный User — длинные.
public static class MonitoringFrequencies
{
    public static string ToCron(MonitoringFrequency f) => f switch
    {
        MonitoringFrequency.Every10Min => "*/10 * * * *",
        MonitoringFrequency.Every30Min => "*/30 * * * *",
        MonitoringFrequency.Daily => "0 9 * * *",
        MonitoringFrequency.Weekly => "0 9 * * 1",
        // Biweekly чистым cron не выражается → аппроксимация «1 и 15 числа»
        // (live-monitoring-plan §7, open decision). TODO: weekly + 14-дневный gate, если понадобится точность.
        MonitoringFrequency.Biweekly => "0 9 1,15 * *",
        MonitoringFrequency.Monthly => "0 9 1 * *",
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unknown monitoring frequency"),
    };

    public static readonly MonitoringFrequency[] AdminAllowed =
    [
        MonitoringFrequency.Every10Min,
        MonitoringFrequency.Every30Min,
        MonitoringFrequency.Daily,
    ];

    public static readonly MonitoringFrequency[] UserAllowed =
    [
        MonitoringFrequency.Daily,
        MonitoringFrequency.Weekly,
        MonitoringFrequency.Biweekly,
        MonitoringFrequency.Monthly,
    ];

    public static bool IsAllowedForRole(MonitoringFrequency f, bool isAdmin)
        => isAdmin ? AdminAllowed.Contains(f) : UserAllowed.Contains(f);

    public static readonly int[] AllowedWindowDays = [7, 30, 90];
}
