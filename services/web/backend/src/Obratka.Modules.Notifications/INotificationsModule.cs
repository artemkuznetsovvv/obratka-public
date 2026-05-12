namespace Obratka.Modules.Notifications;

public interface INotificationsModule
{
    Task SendAdminAlertAsync(string message, string correlationId, CancellationToken ct);
}
