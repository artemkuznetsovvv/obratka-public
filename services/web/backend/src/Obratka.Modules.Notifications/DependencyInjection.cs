using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Obratka.Modules.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName));

        services
            .AddOptions<TelegramProxyOptions>()
            .Bind(configuration.GetSection(TelegramProxyOptions.SectionName));

        // ITelegramBotClient напрямую НЕ регистрируем: им владеет ITelegramClientManager
        // (реализуется в Web API с доступом к пулу прокси webapi_db + ротацией). В чистом
        // модуле/тестах manager не зарегистрирован → NotificationsModule.manager == null → лог-стаб
        // (как было с bot=null). Само построение клиента/прокси — в TelegramProxyClientFactory.
        services.AddScoped<INotificationsModule>(sp =>
            ActivatorUtilities.CreateInstance<NotificationsModule>(sp));

        return services;
    }
}
