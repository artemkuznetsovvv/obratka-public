using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Notifications;

namespace Obratka.WebApi.Controllers;

// Привязка/отвязка Telegram в личном кабинете (ТЗ §1, §4). Сами уведомления шлёт NotificationsModule.
[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(
    WebApiDbContext db,
    UserManager<ApplicationUser> userManager,
    IOptions<TelegramOptions> telegramOptions) : ControllerBase
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);

    // Статус привязки Telegram текущего пользователя + конфигурация бота для UI.
    [HttpGet("telegram/status")]
    [ProducesResponseType(typeof(TelegramStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramStatusDto>> Status(CancellationToken ct)
    {
        var userId = GetUserIdOrNull();
        if (userId is null) return Unauthorized();

        var chatId = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.TelegramChatId)
            .FirstOrDefaultAsync(ct);

        var opts = telegramOptions.Value;
        return Ok(new TelegramStatusDto(
            Linked: !string.IsNullOrWhiteSpace(chatId),
            BotUsername: opts.BotUsername,
            Configured: opts.IsConfigured));
    }

    // Генерирует одноразовый токен и deep-link для привязки: t.me/<bot>?start=<token>.
    [HttpPost("telegram/link")]
    [ProducesResponseType(typeof(TelegramLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TelegramLinkDto>> Link(CancellationToken ct)
    {
        var userId = GetUserIdOrNull();
        if (userId is null) return Unauthorized();

        var opts = telegramOptions.Value;
        if (!opts.IsConfigured || string.IsNullOrWhiteSpace(opts.BotUsername))
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Telegram не настроен",
                detail: "Бот не сконфигурирован на сервере. Обратитесь к администратору.");

        var uid = userId.Value;

        // Чистим прежние токены юзера, генерируем новый.
        var stale = await db.TelegramLinkTokens.Where(t => t.UserId == uid).ToListAsync(ct);
        db.TelegramLinkTokens.RemoveRange(stale);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TokenTtl);
        var token = GenerateToken();
        db.TelegramLinkTokens.Add(new TelegramLinkToken
        {
            Token = token,
            UserId = uid,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct);

        var deepLink = $"https://t.me/{opts.BotUsername}?start={token}";
        return Ok(new TelegramLinkDto(deepLink, expiresAt));
    }

    // Отвязка Telegram (ТЗ §4, подтверждение — на фронте).
    [HttpPost("telegram/unlink")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var userId = GetUserIdOrNull();
        if (userId is null) return Unauthorized();
        var uid = userId.Value;

        var user = await userManager.FindByIdAsync(uid.ToString());
        if (user is null) return Unauthorized();

        user.TelegramChatId = null;
        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded)
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Не удалось отвязать Telegram",
                detail: string.Join("; ", update.Errors.Select(e => e.Description)));

        var stale = await db.TelegramLinkTokens.Where(t => t.UserId == uid).ToListAsync(ct);
        db.TelegramLinkTokens.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

public sealed record TelegramStatusDto(bool Linked, string BotUsername, bool Configured);
public sealed record TelegramLinkDto(string DeepLink, DateTimeOffset ExpiresAt);
