namespace Obratka.WebApi.Monitoring;

// Статус мониторинга. Хранится строкой (HasConversion<string>); в API отдаётся
// в lowercase (active|paused|error) — см. DTO-маппинг.
public enum MonitoringStatus
{
    Active,
    Paused,
    Error,
}

// Статус одного цикла (и last_run_status конфига).
public enum MonitoringCycleStatus
{
    Running,
    Success,
    Partial,
    Failed,
}

// Частота обновления. Набор доступных значений зависит от роли (см. MonitoringScheduler /
// MonitoringsController): Admin — Every10Min/Every30Min/Daily (для тестов), User —
// Daily/Weekly/Biweekly/Monthly. Отклонение от ТЗ §1 (час/6ч/12ч/сутки) — согласовано.
public enum MonitoringFrequency
{
    Every10Min,
    Every30Min,
    Daily,
    Weekly,
    Biweekly,
    Monthly,
}
