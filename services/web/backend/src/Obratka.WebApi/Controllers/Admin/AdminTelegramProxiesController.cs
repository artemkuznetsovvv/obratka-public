using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Notifications;

namespace Obratka.WebApi.Controllers.Admin;

// Админ-CRUD пула прокси Telegram-бота (webapi_db, локально — НЕ проксируем в Parser).
// Зеркало AdminProxiesController, но на ITelegramProxyRepository. Password не возвращаем.
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/telegram-proxies")]
public sealed class AdminTelegramProxiesController(ITelegramProxyRepository repo) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(TelegramProxyListResponse), StatusCodes.Status200OK)]
    public async Task<TelegramProxyListResponse> List([FromQuery] bool? enabledOnly, CancellationToken ct)
    {
        var rows = await repo.ListAsync(enabledOnly, ct);
        var items = rows.Select(ToDto).ToList();
        return new TelegramProxyListResponse(items.Count, items);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TelegramProxyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TelegramProxyDto>> Create(
        [FromBody] CreateTelegramProxyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
            return BadRequest(new { error = "Не указан host прокси" });
        if (request.Port is < 1 or > 65535)
            return BadRequest(new { error = "Порт вне диапазона 1..65535" });
        var protocol = NormalizeProtocol(request.Protocol);
        if (protocol is null)
            return BadRequest(new { error = "Протокол должен быть socks5, http или https" });

        var entity = new TelegramProxy
        {
            Host = request.Host.Trim(),
            Port = request.Port,
            Protocol = protocol,
            Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
            Password = string.IsNullOrWhiteSpace(request.Password) ? null : request.Password,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Enabled = request.Enabled ?? true,
            ExpiresAt = request.ExpiresAt,
        };
        var created = await repo.AddAsync(entity, ct);
        return Created($"api/admin/telegram-proxies/{created.Id}", ToDto(created));
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await repo.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/disable")]
    [ProducesResponseType(typeof(TelegramProxyDto), StatusCodes.Status200OK)]
    public Task<ActionResult<TelegramProxyDto>> Disable(int id, CancellationToken ct) => SetEnabled(id, false, ct);

    [HttpPost("{id:int}/enable")]
    [ProducesResponseType(typeof(TelegramProxyDto), StatusCodes.Status200OK)]
    public Task<ActionResult<TelegramProxyDto>> Enable(int id, CancellationToken ct) => SetEnabled(id, true, ct);

    [HttpPost("{id:int}/reset-health")]
    [ProducesResponseType(typeof(TelegramProxyDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramProxyDto>> ResetHealth(int id, CancellationToken ct)
    {
        await repo.ResetHealthAsync(id, ct);
        return await GetDtoOrNotFound(id, ct);
    }

    [HttpPost("{id:int}/set-expires-at")]
    [ProducesResponseType(typeof(TelegramProxyDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TelegramProxyDto>> SetExpiresAt(
        int id, [FromBody] SetTelegramProxyExpiresAtRequest request, CancellationToken ct)
    {
        await repo.SetExpiresAtAsync(id, request.ExpiresAt, ct);
        return await GetDtoOrNotFound(id, ct);
    }

    private async Task<ActionResult<TelegramProxyDto>> SetEnabled(int id, bool enabled, CancellationToken ct)
    {
        await repo.SetEnabledAsync(id, enabled, ct);
        return await GetDtoOrNotFound(id, ct);
    }

    private async Task<ActionResult<TelegramProxyDto>> GetDtoOrNotFound(int id, CancellationToken ct)
    {
        var e = await repo.GetByIdAsync(id, ct);
        if (e is null) return NotFound();
        return ToDto(e);
    }

    private static string? NormalizeProtocol(string? p)
    {
        var v = (p ?? "socks5").Trim().ToLowerInvariant();
        return v is "socks5" or "http" or "https" ? v : null;
    }

    // Password НИКОГДА не отдаём наружу (как ParserProxyDto — только Username).
    private static TelegramProxyDto ToDto(TelegramProxy p) => new(
        p.Id, p.Host, p.Port, p.Protocol, p.Username, p.Enabled, p.FailureCount,
        p.CooldownUntil, p.LastUsedAt, p.ExpiresAt, p.Notes, p.CreatedAt, p.UpdatedAt);
}
