namespace Obratka.WebApi.Notifications;

// Прокси Telegram-бота (webapi_db, таблица telegram_proxies). Зеркало парсерного ProxyEntity,
// но дефолтный протокол socks5 (обход блокировки api.telegram.org на хостинге). Пул ротируется
// WebApiTelegramClientManager при connectivity-сбое long-poll.
public class TelegramProxy
{
    public int Id { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string Protocol { get; set; } = "socks5";
    public bool Enabled { get; set; } = true;
    public int FailureCount { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
