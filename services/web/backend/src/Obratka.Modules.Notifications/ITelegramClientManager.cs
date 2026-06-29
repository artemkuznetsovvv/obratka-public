using Telegram.Bot;

namespace Obratka.Modules.Notifications;

// Владелец Telegram-клиента с ротацией прокси. Объявлен в модуле (без зависимости от БД),
// реализован в Web API (WebApiTelegramClientManager) с доступом к пулу прокси в webapi_db —
// тот же seam, что INotificationRecipientResolver. И sender (NotificationsModule), и receiver
// (TelegramUpdateListener) берут активный клиент через Current; при connectivity-сбое long-poll
// receiver зовёт RotateAsync, чтобы переключиться на следующий доступный прокси.
public interface ITelegramClientManager
{
    // Активный клиент (под текущим прокси). null — если канал не сконфигурирован либо нет ни
    // одного пригодного прокси и нет fallback (всё в cooldown/выключено).
    ITelegramBotClient? Current { get; }

    // Лениво собрать клиент, если ещё не собран (БЕЗ пометки прокси упавшим). Возвращает,
    // есть ли пригодный клиент.
    Task<bool> EnsureCurrentAsync(CancellationToken ct);

    // Пометить текущий прокси упавшим (cooldown), выбрать следующий доступный и пересобрать
    // клиент. Возвращает, есть ли пригодный клиент после ротации.
    Task<bool> RotateAsync(string reason, CancellationToken ct);
}
