namespace Obratka.Modules.Notifications;

// Конфиг Telegram-канала (секция "Telegram" в appsettings / секретах).
// Канал включается только когда Enabled=true И задан BotToken — иначе модуль работает как
// лог-стаб (ничего не отправляет, только пишет в Serilog), чтобы dev-стенд поднимался без бота.
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }

    // Токен бота от @BotFather.
    public string BotToken { get; set; } = string.Empty;

    // Username бота без @ (для deep-link https://t.me/<username>?start=<token>).
    public string BotUsername { get; set; } = string.Empty;

    // Chat-id системных администраторов (получают все error-алерты; ручная подписка не нужна).
    // Можно задавать массивом (JSON / индексы env __0,__1) ИЛИ перечислением через запятую
    // в одном значении — см. ResolvedAdminChatIds.
    public List<string> AdminChatIds { get; set; } = new();

    // Эффективный список: каждый элемент дополнительно режется по запятым, чтобы в одной
    // env-переменной (Telegram__AdminChatIds__0="id1,id2") можно было задать несколько id.
    // Пустые значения и дубли отбрасываются.
    public IReadOnlyList<string> ResolvedAdminChatIds =>
        AdminChatIds
            .SelectMany(s => (s ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

    // Базовый URL фронта для абсолютной ссылки «Открыть дашборд» в сообщении (напр. http://localhost:5173).
    public string DashboardBaseUrl { get; set; } = string.Empty;

    // Прокси для обхода блокировки api.telegram.org на хостинге: socks5://host:port или http://host:port.
    // Пусто → прямой доступ (dev) либо VPN-route на уровне хоста/контейнера.
    public string? Proxy { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BotToken);
}
