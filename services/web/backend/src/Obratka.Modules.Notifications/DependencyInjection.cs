using Microsoft.Extensions.DependencyInjection;

namespace Obratka.Modules.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationsModule, NotificationsModule>();
        return services;
    }
}
