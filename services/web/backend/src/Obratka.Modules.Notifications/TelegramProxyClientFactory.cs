using System.Net;
using Telegram.Bot;

namespace Obratka.Modules.Notifications;

// Единая сборка ITelegramBotClient с опциональным прокси (socks5/http/https). Используется и
// fallback-путём (строка Telegram:Proxy в WebApiTelegramClientManager), и при выборе прокси из
// пула webapi_db. Один источник BuildProxy — без дублей (раньше был private в DependencyInjection).
public static class TelegramProxyClientFactory
{
    // Клиент с прокси из строки "scheme://[user:pass@]host:port" (null/пусто → прямое соединение).
    public static ITelegramBotClient Build(string token, string? proxyRaw)
    {
        if (string.IsNullOrWhiteSpace(proxyRaw))
            return new TelegramBotClient(token);

        var handler = new HttpClientHandler
        {
            Proxy = BuildProxy(proxyRaw),
            UseProxy = true,
        };
        return new TelegramBotClient(token, new HttpClient(handler));
    }

    // Клиент по частям прокси (из БД): компонует "scheme://user:pass@host:port" с URL-экранированием
    // логина/пароля и делегирует в Build(token, raw).
    public static ITelegramBotClient Build(
        string token, string protocol, string host, int port, string? username, string? password)
        => Build(token, ComposeProxyUri(protocol, host, port, username, password));

    public static string ComposeProxyUri(
        string protocol, string host, int port, string? username, string? password)
    {
        var scheme = string.IsNullOrWhiteSpace(protocol) ? "socks5" : protocol.Trim();
        var auth = string.IsNullOrEmpty(username)
            ? ""
            : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password ?? "")}@";
        return $"{scheme}://{auth}{host}:{port}";
    }

    // Прокси из строки: "scheme://host:port" или "scheme://user:pass@host:port" (http/https/socks5).
    // Логин/пароль из userinfo кладём в Credentials — WebProxy не использует userinfo как креды.
    public static WebProxy BuildProxy(string raw)
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
