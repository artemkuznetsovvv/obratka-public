namespace Obratka.WebApi.Scheduling;

// Recurring-джоба: отслеживает запущенные разовые анализы и по достижении терминального статуса
// шлёт уведомление (готово — пользователю, ошибка — админу). Не зависит от открытого UI.
public interface IAnalysisNotificationReconciler
{
    Task ReconcileAsync();
}
