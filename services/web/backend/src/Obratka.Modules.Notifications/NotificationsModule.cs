namespace Obratka.Modules.Notifications;

internal sealed class NotificationsModule : INotificationsModule
{
    public Task SendAdminAlertAsync(string message, string correlationId, CancellationToken ct)
        => throw new NotImplementedException();
}
