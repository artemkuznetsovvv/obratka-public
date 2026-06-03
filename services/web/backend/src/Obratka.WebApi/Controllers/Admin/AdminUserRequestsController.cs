using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Support;

namespace Obratka.WebApi.Controllers.Admin;

// Борда обращений пользователей (сейчас — запросы на сброс пароля). Админ видит список,
// меняет пароль через /api/admin/users/{id}/set-password и помечает запрос обработанным.
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/user-requests")]
public sealed class AdminUserRequestsController(
    WebApiDbContext db,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AdminUserRequestListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminUserRequestListResponse>> List(
        [FromQuery] string? status, CancellationToken ct)
    {
        var query = db.UserRequests.AsNoTracking();
        if (string.Equals(status, "new", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == UserRequestStatus.New);
        else if (string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == UserRequestStatus.Resolved);

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(500)
            .ToListAsync(ct);

        var items = rows
            .Select(r => new AdminUserRequestDto(
                r.Id,
                r.Type.ToString().ToLowerInvariant(),
                r.Email,
                r.UserId,
                r.Message,
                r.Status.ToString().ToLowerInvariant(),
                r.CreatedAt,
                r.ResolvedAt))
            .ToList();

        var newCount = await db.UserRequests.CountAsync(r => r.Status == UserRequestStatus.New, ct);
        return Ok(new AdminUserRequestListResponse(newCount, items));
    }

    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(Guid id, CancellationToken ct)
    {
        var req = await db.UserRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (req is null) return NotFound();

        if (req.Status != UserRequestStatus.Resolved)
        {
            req.Status = UserRequestStatus.Resolved;
            req.ResolvedAt = DateTimeOffset.UtcNow;
            req.ResolvedByUserId = Guid.TryParse(userManager.GetUserId(User), out var adminId) ? adminId : null;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
