using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace Obratka.Modules.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName));

        var tg = configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();

        // ITelegramBotClient — синглтон, только если канал сконфигурирован (Enabled + токен).
        // Иначе NotificationsModule получит bot=null и будет работать лог-стабом. Один клиент
        // используется И отправкой, И long-poll receiver'ом (см. TelegramUpdateListener в WebApi).
        if (tg.IsConfigured)
        {
            services.AddSingleton<ITelegramBotClient>(_ =>
            {
                HttpClient? http = null;
                if (!string.IsNullOrWhiteSpace(tg.Proxy))
                {
                    // Обход блокировки api.telegram.org на хостинге: socks5://… или http://….
                    var handler = new HttpClientHandler
                    {
                        Proxy = BuildProxy(tg.Proxy),
                        UseProxy = true,
                    };
                    http = new HttpClient(handler);
                }
                return new TelegramBotClient(tg.BotToken, http);
            });
        }

        // CreateInstance: опциональный ITelegramBotClient? bot=null подставится дефолтом,
        // если клиент не зарегистрирован (как у MonitoringCycleRunner с Analytics-сервисами).
        services.AddScoped<INotificationsModule>(sp =>
            ActivatorUtilities.CreateInstance<NotificationsModule>(sp));

        return services;
    }

    // Прокси из конфига: "scheme://host:port" или "scheme://user:pass@host:port".
    // Поддержка http/https/socks5. Логин/пароль (если есть в URL) кладём в Credentials —
    // в самом адресе прокси userinfo оставлять нельзя (WebProxy его не использует как креды).
    private static WebProxy BuildProxy(string raw)
    {
        var uri = new Uri(raw);
        var proxy = new WebProxy(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var creds = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(creds[0]),
                creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : string.Empty);
        }
        return proxy;
    }
}
