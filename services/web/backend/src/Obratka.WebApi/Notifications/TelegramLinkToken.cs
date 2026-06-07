namespace Obratka.WebApi.Notifications;

// Одноразовый токен привязки Telegram. Создаётся, когда пользователь жмёт «Подключить Telegram»
// в кабинете; кладётся в deep-link https://t.me/<bot>?start=<token>. Бот при /start <token>
// резолвит его в UserId и пишет chat_id в ApplicationUser.TelegramChatId, затем токен удаляется.
// Хранение в БД (а не в памяти) — переживает рестарт и аудируется. TTL ~15 минут.
public class TelegramLinkToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Сам токен (url-safe, ~32 символа). Уникальный.
    public string Token { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
