namespace Obratka.WebApi.Notifications;

// Трекер разового analysis_job для уведомления по готовности. Создаётся при запуске анализа
// (user/admin контроллеры); фоновая reconcile-джоба (AnalysisNotificationReconciler) поллит
// статус в PG и по достижении терминального статуса шлёт уведомление, проставляя NotifiedAt —
// после чего job больше не отслеживается. Так пинг приходит независимо от того, открыт ли UI.
public class AnalysisNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // analysis_job в Processing-Gateway.
    public Guid JobId { get; set; }

    // Получатель уведомления «готово» — владелец компании (а не тот, кто запустил, если это админ).
    public Guid UserId { get; set; }
    public Guid CompanyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Проставляется, когда уведомление отправлено (или мы перестали отслеживать job). null = ещё ждём.
    public DateTimeOffset? NotifiedAt { get; set; }
}
