namespace Obratka.Modules.Notifications;

// Структурный admin-алерт об ошибке (ТЗ §3 — минимальный состав полей).
// Severity: "critical" | "warning". EventId — для поиска в централизованных логах (Seq).
public sealed record AdminAlert(
    string Stage,             // Сбор | Анализ | Отчёт | Мониторинг
    string Reason,            // краткая причина
    string Severity,          // critical | warning
    string EventId,           // Guid("N") — id события для логов
    Guid? UserId = null,
    string? UserLabel = null, // email/аккаунт, если известен
    Guid? CompanyId = null,
    string? CompanyName = null,
    Guid? JobId = null);
