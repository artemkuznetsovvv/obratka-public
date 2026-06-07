using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Obratka.Modules.Notifications;

// Доставка уведомлений в Telegram. Канал опционален: если ITelegramBotClient не зарегистрирован
// (нет токена/Enabled=false), модуль НЕ падает — пишет в Serilog как лог-стаб. Все исходящие
// запросы логируются ([telegram:send] ...). Сбой отправки (бот заблокирован=403, rate-limit=429,
// любой иной) фиксируется в логе БЕЗ бесконечных ретраев — мониторинг продолжает работать (ТЗ §5).
internal sealed class NotificationsModule(
    INotificationRecipientResolver recipients,
    IOptions<TelegramOptions> options,
    ILogger<NotificationsModule> logger,
    ITelegramBotClient? bot = null) : INotificationsModule
{
    private TelegramOptions Opts => options.Value;

    public async Task SendMonitoringCycleResultAsync(
        Guid userId, Guid monitoringId, string status, int newReviewCount,
        DateTimeOffset? periodFrom, DateTimeOffset periodTo,
        IReadOnlyList<string> unavailableSources, CancellationToken ct)
    {
        // Анти-спам: тихо пропускаем только чистый успех без новинок (success/no_new).
        // failed/partial всегда доходят до пользователя (ТЗ §2), даже без перечня источников.
        if (newReviewCount <= 0 && unavailableSources.Count == 0 && status is "success" or "no_new")
        {
            logger.LogDebug("[notify:user] cycle no-op (monitoring={MonitoringId}, status={Status}) — skip send",
                monitoringId, status);
            return;
        }

        UserNotificationTarget? target;
        try
        {
            target = await recipients.ResolveUserAsync(userId, monitoringId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[notify:user] resolve failed (cycle-result, monitoring={MonitoringId})", monitoringId);
            return;
        }
        if (target is null)
        {
            logger.LogWarning("[notify:user] target not found (cycle-result, monitoring={MonitoringId})", monitoringId);
            return;
        }

        // Получатели: личный чат владельца (если подписка вкл и привязан) + доп. чаты компании (всегда).
        var recipientsList = BuildRecipients(target.NotificationsEnabled ? target.ChatId : null, target.ExtraChatIds);
        if (NoDelivery(recipientsList, "cycle-result", monitoringId.ToString("N"))) return;

        var period = periodFrom is { } f
            ? $"{f:dd.MM.yyyy}–{periodTo:dd.MM.yyyy}"
            : $"по {periodTo:dd.MM.yyyy}";

        // Заголовок отражает исход — не врём «выполнено» при провале/частичном обновлении.
        var header = status switch
        {
            "failed" => $"⚠️ Обновление не выполнено — {Esc(target.CompanyName)}",
            "partial" => $"🔄 Обновление выполнено частично — {Esc(target.CompanyName)}",
            _ => $"🔄 Обновление выполнено — {Esc(target.CompanyName)}",
        };
        var lines = new List<string>
        {
            $"<b>{header}</b>",
            $"Период: {period}",
        };
        if (newReviewCount > 0)
            lines.Add($"🆕 +{newReviewCount} {Plural(newReviewCount, "новый отзыв", "новых отзыва", "новых отзывов")}");
        else if (status == "failed")
            lines.Add("Не удалось завершить обновление за этот цикл.");
        else
            lines.Add("Новых отзывов нет");
        if (unavailableSources.Count > 0)
            lines.Add($"⚠️ Источники недоступны: {Esc(string.Join(", ", unavailableSources))}");

        var text = string.Join("\n", lines);
        var button = DashboardButton(target.SeedJobId, monitoringId);
        foreach (var chatId in recipientsList)
            await SendRawAsync(chatId, text, button, "cycle-result", monitoringId.ToString("N"), ct);
    }

    public async Task SendAnalysisReadyAsync(
        Guid userId, Guid companyId, Guid jobId, string companyName, int reviewCount, CancellationToken ct)
    {
        AnalysisRecipients rec;
        try
        {
            rec = await recipients.ResolveAnalysisRecipientsAsync(userId, companyId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[notify:user] resolve failed (analysis-ready, job={JobId})", jobId);
            return;
        }

        var recipientsList = BuildRecipients(rec.OwnerChatId, rec.ExtraChatIds);
        if (NoDelivery(recipientsList, "analysis-ready", jobId.ToString("N"))) return;

        var text =
            $"<b>✅ Анализ готов — {Esc(companyName)}</b>\n" +
            $"Собрано отзывов: {reviewCount}";
        var button = DashboardButtonForPath($"/history/{jobId}/dashboard");
        foreach (var chatId in recipientsList)
            await SendRawAsync(chatId, text, button, "analysis-ready", jobId.ToString("N"), ct);
    }

    public async Task SendNegativeSentimentAlertAsync(
        Guid userId, Guid monitoringId, double previousNegativePp, double currentNegativePp,
        int newReviewCount, CancellationToken ct)
    {
        UserNotificationTarget? target;
        try
        {
            target = await recipients.ResolveUserAsync(userId, monitoringId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[notify:user] resolve failed (negative-spike, monitoring={MonitoringId})", monitoringId);
            return;
        }
        if (target is null)
        {
            logger.LogWarning("[notify:user] target not found (negative-spike, monitoring={MonitoringId})", monitoringId);
            return;
        }

        var recipientsList = BuildRecipients(target.NotificationsEnabled ? target.ChatId : null, target.ExtraChatIds);
        if (NoDelivery(recipientsList, "negative-spike", monitoringId.ToString("N"))) return;

        var text =
            $"<b>📈 Резкий рост негатива — {Esc(target.CompanyName)}</b>\n" +
            $"Доля негатива: {previousNegativePp:0.#}% → {currentNegativePp:0.#}%\n" +
            $"За цикл: {newReviewCount} {Plural(newReviewCount, "новый отзыв", "новых отзыва", "новых отзывов")}";
        var button = DashboardButton(target.SeedJobId, monitoringId);
        foreach (var chatId in recipientsList)
            await SendRawAsync(chatId, text, button, "negative-spike", monitoringId.ToString("N"), ct);
    }

    public async Task SendAdminAlertAsync(AdminAlert alert, CancellationToken ct)
    {
        var statusLabel = alert.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
            ? "🔴 критично" : "🟡 не критично";

        var lines = new List<string>
        {
            $"<b>⚠️ Ошибка: {Esc(alert.Stage)}</b>",
            $"Статус: {statusLabel}",
            $"Причина: {Esc(alert.Reason)}",
        };
        if (alert.UserId is not null || alert.UserLabel is not null)
            lines.Add($"Пользователь: {Esc(alert.UserLabel ?? alert.UserId?.ToString() ?? "—")}");
        if (alert.CompanyId is not null || alert.CompanyName is not null)
            lines.Add($"Компания: {Esc(alert.CompanyName ?? alert.CompanyId?.ToString() ?? "—")}");
        if (alert.JobId is not null)
            lines.Add($"Анализ: <code>{alert.JobId}</code>");
        lines.Add($"event: <code>{Esc(alert.EventId)}</code>");
        var text = string.Join("\n", lines);

        var admins = Opts.ResolvedAdminChatIds;
        if (bot is null || admins.Count == 0)
        {
            logger.LogWarning(
                "[notify:admin] (stub) stage={Stage} severity={Severity} event={EventId} reason={Reason} " +
                "user={UserId} company={CompanyId} job={JobId}",
                alert.Stage, alert.Severity, alert.EventId, alert.Reason, alert.UserId, alert.CompanyId, alert.JobId);
            return;
        }

        foreach (var chatId in admins)
            await SendRawAsync(chatId, text, replyMarkup: null, "admin-alert", alert.EventId, ct);
    }

    // ----- helpers -----

    // Финальный список получателей: личный чат владельца (если есть) + доп. чаты компании,
    // без пустых и дублей. Порядок: владелец первым.
    private static List<string> BuildRecipients(string? ownerChatId, IReadOnlyList<string> extra)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s);
        }
        Add(ownerChatId);
        foreach (var e in extra) Add(e);
        return list;
    }

    // true → доставлять некому (бот выключен или нет ни одного чата). Логирует причину.
    private bool NoDelivery(IReadOnlyList<string> recipients, string type, string corr)
    {
        if (bot is not null && recipients.Count > 0) return false;
        logger.LogInformation(
            "[notify:user] нет получателей (type={Type}, corr={Corr}, recipients={Count}, botEnabled={BotEnabled}) — skip",
            type, corr, recipients.Count, bot is not null);
        return true;
    }

    // Единственная точка отправки: логирует каждый исходящий запрос; ошибки — без ретраев.
    private async Task SendRawAsync(
        string chatId, string text, InlineKeyboardMarkup? replyMarkup, string type, string corr, CancellationToken ct)
    {
        if (bot is null)
        {
            logger.LogInformation("[telegram:send] type={Type} skipped (bot disabled) corr={Corr}", type, corr);
            return;
        }
        try
        {
            await bot.SendMessage(
                chatId: new ChatId(chatId),
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: ct);
            logger.LogInformation("[telegram:send] type={Type} chatId={ChatId} ok corr={Corr}", type, chatId, corr);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            // 403 = канал недоступен (бот заблокирован / юзер деактивирован / kicked из группы и т.п.).
            // Без ретраев (ТЗ §5); точную причину берём из ответа Telegram.
            logger.LogWarning(
                "[telegram:send] type={Type} chatId={ChatId} канал недоступен code=403 reason={Reason} corr={Corr}",
                type, chatId, ex.Message, corr);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            logger.LogWarning(
                "[telegram:send] type={Type} chatId={ChatId} rate-limit code=429 retryAfter={RetryAfter}s corr={Corr} — без ретраев",
                type, chatId, ex.Parameters?.RetryAfter, corr);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[telegram:send] type={Type} chatId={ChatId} error corr={Corr}", type, chatId, corr);
        }
    }

    private InlineKeyboardMarkup? DashboardButton(Guid seedJobId, Guid monitoringId)
        => DashboardButtonForPath($"/history/{seedJobId}/dashboard?monitoring={monitoringId}");

    // Кнопка «Открыть дашборд» на относительном пути. Telegram требует валидный абсолютный
    // http(s) URL для inline-кнопки; иначе ВЕСЬ SendMessage упадёт (BUTTON_URL_INVALID) — поэтому
    // при мисконфиге base-url деградируем (сообщение без кнопки), не роняя доставку.
    private InlineKeyboardMarkup? DashboardButtonForPath(string path)
    {
        var baseUrl = Opts.DashboardBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var trimmed = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("[telegram] DashboardBaseUrl невалиден ('{BaseUrl}') — кнопка дашборда пропущена", baseUrl);
            return null;
        }
        return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("📊 Открыть дашборд", $"{trimmed}{path}"));
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    private static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return one;
        if (mod10 is >= 2 and <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
        return many;
    }
}
