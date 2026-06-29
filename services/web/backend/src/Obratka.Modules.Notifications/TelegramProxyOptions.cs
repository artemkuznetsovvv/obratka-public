namespace Obratka.Modules.Notifications;

// Политика пула прокси Telegram. Осознанное отклонение от парсерной ротации (3 страйка → 300с):
// для long-poll «упал → сразу сменить» — connectivity-ошибка ротирует немедленно (порог 1) и
// паркует прокси на короткий cooldown, чтобы пикер не выбрал его тут же.
public sealed class TelegramProxyOptions
{
    public const string SectionName = "TelegramProxy";

    // Сколько провалов на одном прокси до cooldown. 1 = cooldown сразу при первом сбое.
    public int MaxFailuresBeforeCooldown { get; set; } = 1;

    // Длительность cooldown упавшего прокси (сек). Пока активен — прокси не выбирается.
    public int CooldownSeconds { get; set; } = 120;

    // Бэк-офф receiver'а, когда нет ни одного пригодного прокси (все в cooldown/выключены).
    public int NoProxyRetryDelaySeconds { get; set; } = 30;
}
