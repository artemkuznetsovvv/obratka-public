using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Obratka.WebApi.Notifications;

// Long-poll receiver (ТЗ §1): принимает /start <token> для привязки аккаунта и /unlink для отвязки.
// Дополнительно: /chatid (узнать chat_id — удобно для добавления группы в Telegram:AdminChatIds),
// /wisdom и фраза «Обратка, мудрость» — тестовая команда (бот отвечает мыслью, что он жив).
// Регистрируется только когда Telegram сконфигурирован (см. Program.cs).
// Каждый апдейт обрабатывается в своём scope (UserManager/DbContext — scoped).
//
// Клиент берём у ITelegramClientManager (он же владеет пулом прокси). Вместо fire-and-forget
// StartReceiving ведём СВОЙ цикл ReceiveAsync: на connectivity-ошибке long-poll прерываем петлю
// (отменяем loopCts из error-handler'а), ротируем прокси и перезапускаемся на новом клиенте —
// иначе библиотека вечно ретраила бы мёртвый прокси (баг «Connection refused»).
internal sealed class TelegramUpdateListener(
    ITelegramClientManager manager,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramOptions> options,
    IOptions<TelegramProxyOptions> proxyOptions,
    ILogger<TelegramUpdateListener> logger) : BackgroundService
{
    // Тестовые «мудрости» — для проверки, что бот жив (в т.ч. в группе).
    private static readonly string[] Wisdoms =
    [
        "Сначала собери отзывы — потом делай выводы. Данные мудрее интуиции.",
        "Негатив — это карта, на которой отмечены места для роста.",
        "Один отзыв — мнение. Сто отзывов — закономерность.",
        "Молчание клиента бывает громче любой жалобы.",
        "Не бойся плохих отзывов — бойся их не услышать.",
        "Качество — это не действие, а привычка. (Аристотель)",
        "Кто перестаёт становиться лучше, перестаёт быть хорошим. (О. Кромвель)",
        "Лучший способ предсказать будущее — измерять настоящее.",
        "Доверие строится годами и теряется одним неотвеченным отзывом.",
        "Мудрость начинается с удивления. (Сократ)",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };
        var noProxyDelay = TimeSpan.FromSeconds(Math.Max(5, proxyOptions.Value.NoProxyRetryDelaySeconds));

        await manager.EnsureCurrentAsync(stoppingToken);
        logger.LogInformation("Telegram long-poll receiver запущен (бот @{Username}).", options.Value.BotUsername);

        while (!stoppingToken.IsCancellationRequested)
        {
            var client = manager.Current;
            if (client is null)
            {
                logger.LogWarning("Telegram: нет пригодного прокси — повтор через {Delay}s", noProxyDelay.TotalSeconds);
                try { await Task.Delay(noProxyDelay, stoppingToken); } catch (OperationCanceledException) { break; }
                await manager.RotateAsync("retry-after-empty", stoppingToken);
                continue;
            }

            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            string? rotateReason = null;

            // У ReceiveAsync error-handler без HandleErrorSource (он только у StartReceiving) — нам source
            // не нужен, классифицируем по типу исключения. На connectivity-ошибке отменяем loopCts
            // (return-значением петлю не прервать) → ReceiveAsync завершится, ниже ротируем. API-ошибки
            // (429/неверный токен) НЕ ротируем — смена прокси их не лечит. Явно типизируем делегат, чтобы
            // не было неоднозначности перегрузок.
            Func<ITelegramBotClient, Exception, CancellationToken, Task> errorHandler = (_, ex, _) =>
            {
                if (IsConnectivityError(ex))
                {
                    logger.LogWarning(ex, "Telegram long-poll connectivity error — ротация прокси");
                    rotateReason = $"long-poll: {ex.GetType().Name}";
                    // ReSharper disable once AccessToDisposedClosure — отмена до выхода из ReceiveAsync.
                    loopCts.Cancel();
                }
                else
                {
                    logger.LogWarning(ex, "Telegram long-poll error");
                }
                return Task.CompletedTask;
            };

            try
            {
                await client.ReceiveAsync(HandleUpdateAsync, errorHandler, receiverOptions, loopCts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // остановка хоста — выходим штатно
            }
            catch (OperationCanceledException)
            {
                // loopCts отменён error-handler'ом на connectivity-ошибке → ротируем ниже.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram receive loop unexpected error — ротация прокси");
                rotateReason ??= $"receive-loop: {ex.GetType().Name}";
            }

            if (stoppingToken.IsCancellationRequested) break;
            await manager.RotateAsync(rotateReason ?? "long-poll-restart", stoppingToken);

            // Троттлинг рестартов: если ротация снова даёт падающий клиент (классический случай —
            // единственный fallback-прокси из конфига мёртв и пул пуст: cooldown к fallback неприменим,
            // Current не становится null), без паузы это был бы горячий цикл. Короткая пауза ограничивает
            // частоту перезапусков; на исправную ротацию между живыми прокси влияет незначительно.
            try { await Task.Delay(RestartThrottle, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    // Минимальный интервал между перезапусками receive-loop после connectivity-сбоя (анти-hot-loop).
    private static readonly TimeSpan RestartThrottle = TimeSpan.FromSeconds(5);

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message) return;
        var chatId = message.Chat.Id;
        var isPrivate = message.Chat.Type == ChatType.Private;
        var trimmed = text.Trim();
        // В группах команда может прийти как "/cmd@botusername" — отрезаем суффикс.
        var lower = trimmed.ToLowerInvariant();

        try
        {
            if (lower.StartsWith("/start"))
            {
                await HandleStartAsync(client, chatId, trimmed, isPrivate, ct);
            }
            else if (lower.StartsWith("/unlink") || lower.StartsWith("/stop"))
            {
                await HandleUnlinkAsync(client, chatId, ct);
            }
            else if (lower.StartsWith("/chatid"))
            {
                await client.SendMessage(chatId,
                    $"chat_id этого чата: <code>{chatId}</code>\n" +
                    "Добавьте его в <b>Telegram:AdminChatIds</b>, чтобы сюда приходили системные алерты.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
            }
            else if (lower.StartsWith("/testalert"))
            {
                await HandleTestAlertAsync(client, chatId, ct);
            }
            else if (IsWisdomTrigger(lower))
            {
                await client.SendMessage(chatId, $"🧠 {RandomWisdom()}", cancellationToken: ct);
            }
            else if (isPrivate)
            {
                // В личке отвечаем подсказкой; в группах молчим, чтобы не спамить на каждое сообщение.
                await client.SendMessage(chatId,
                    "Я бот уведомлений «Обратки». Чтобы привязать аккаунт, нажмите «Подключить Telegram» в личном кабинете.\n" +
                    "Команды: /wisdom — мысль для проверки, /chatid — узнать id чата, /unlink — отвязать.",
                    cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram update handling failed (chat={ChatId})", chatId);
        }
    }

    private async Task HandleStartAsync(
        ITelegramBotClient client, long chatId, string text, bool isPrivate, CancellationToken ct)
    {
        // "/start <token>" — payload из deep-link. Без токена — инструкция.
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            await client.SendMessage(chatId,
                "Чтобы привязать аккаунт, откройте личный кабинет «Обратки» и нажмите «Подключить Telegram».",
                cancellationToken: ct);
            return;
        }
        var token = parts[1];

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebApiDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var link = await db.TelegramLinkTokens.FirstOrDefaultAsync(t => t.Token == token, ct);
        if (link is null || link.ExpiresAt < DateTimeOffset.UtcNow)
        {
            if (link is not null)
            {
                db.TelegramLinkTokens.Remove(link);
                await db.SaveChangesAsync(ct);
            }
            await client.SendMessage(chatId,
                "Ссылка устарела или недействительна. Сгенерируйте новую в личном кабинете.",
                cancellationToken: ct);
            return;
        }

        var chatIdStr = chatId.ToString();

        // Один чат — один аккаунт: отвязываем прежних владельцев этого chat_id (перепривязка),
        // иначе уведомления текли бы в общий чат (утечка между аккаунтами). Коммитим отдельно,
        // чтобы не словить конфликт уникального индекса в одной транзакции.
        var prevOwners = await db.Users
            .Where(u => u.TelegramChatId == chatIdStr && u.Id != link.UserId)
            .ToListAsync(ct);
        if (prevOwners.Count > 0)
        {
            foreach (var p in prevOwners) p.TelegramChatId = null;
            await db.SaveChangesAsync(ct);
        }

        var user = await users.FindByIdAsync(link.UserId.ToString());
        if (user is null)
        {
            db.TelegramLinkTokens.Remove(link);
            await db.SaveChangesAsync(ct);
            await client.SendMessage(chatId, "Пользователь не найден.", cancellationToken: ct);
            return;
        }

        user.TelegramChatId = chatIdStr;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded)
        {
            logger.LogError("Telegram link save failed: user={UserId} errors={Errors}",
                link.UserId, string.Join("; ", update.Errors.Select(e => e.Description)));
            await client.SendMessage(chatId, "Не удалось привязать аккаунт, попробуйте позже.", cancellationToken: ct);
            return;
        }

        // Все токены этого юзера больше не нужны.
        var stale = await db.TelegramLinkTokens.Where(t => t.UserId == link.UserId).ToListAsync(ct);
        db.TelegramLinkTokens.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Telegram привязан: user={UserId} chat={ChatId}", link.UserId, chatId);
        var note = isPrivate ? "" : "\n(Привязка к групповому чату — уведомления будут приходить сюда.)";
        await client.SendMessage(chatId,
            "✅ Telegram привязан. Сюда будут приходить уведомления по вашим мониторингам." + note,
            cancellationToken: ct);
    }

    private async Task HandleUnlinkAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebApiDbContext>();
        var idStr = chatId.ToString();
        // Отвязываем ВСЕ аккаунты, привязанные к этому чату (на случай старых дублей).
        var matches = await db.Users.Where(u => u.TelegramChatId == idStr).ToListAsync(ct);
        if (matches.Count == 0)
        {
            await client.SendMessage(chatId, "Этот чат не привязан к аккаунту.", cancellationToken: ct);
            return;
        }
        foreach (var u in matches) u.TelegramChatId = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Telegram отвязан через бота: chat={ChatId} ({Count} аккаунт(ов))", chatId, matches.Count);
        await client.SendMessage(chatId, "Telegram отвязан. Уведомления приходить не будут.", cancellationToken: ct);
    }

    // Диагностика: /testalert из admin-чата шлёт тестовый admin-алерт тем же путём, что и реальные
    // ошибки (SendAdminAlertAsync → все Telegram:AdminChatIds). Доступно только из чатов в AdminChatIds.
    private async Task HandleTestAlertAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!options.Value.ResolvedAdminChatIds.Contains(chatId.ToString()))
        {
            await client.SendMessage(chatId,
                "Команда доступна только администратору (этот chat_id не в Telegram:AdminChatIds).",
                cancellationToken: ct);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationsModule>();
        await notifications.SendAdminAlertAsync(new AdminAlert(
            Stage: "Тест",
            Reason: "Проверка канала admin-уведомлений (команда /testalert).",
            Severity: "warning",
            EventId: Guid.NewGuid().ToString("N")), ct);

        await client.SendMessage(chatId,
            "✅ Тестовый admin-алерт отправлен на все Telegram:AdminChatIds.", cancellationToken: ct);
    }

    private static bool IsWisdomTrigger(string lower)
        => lower.StartsWith("/wisdom")
           || (lower.Contains("обратка") && lower.Contains("мудрост"));

    private static string RandomWisdom() => Wisdoms[Random.Shared.Next(Wisdoms.Length)];

    // Connectivity-ошибка (прокси/сеть мертвы) → ротируем прокси. Классифицируем по ТИПУ по цепочке
    // InnerException, НЕ по тексту (локализация). НЕ включаем Telegram.Bot RequestException, т.к. от
    // него наследуется ApiRequestException (429/неверный токен) — её ротацией не вылечить. Кейс
    // пользователя (RequestException → HttpRequestException → SocketException) ловится через HttpRequestException.
    private static bool IsConnectivityError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException
                or System.Net.Http.HttpRequestException
                or System.Net.WebException
                or System.IO.IOException)
                return true;
        }
        return false;
    }
}
