using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Admin;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/users")]
public sealed class AdminUsersController(
    UserManager<ApplicationUser> userManager,
    WebApiDbContext db,
    IRefreshTokenStore refreshStore,
    ILogger<AdminUsersController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AdminUserListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminUserListResponse>> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(offset, 0);
        var query = db.Users.AsNoTracking().OrderByDescending(u => u.CreatedAt);
        var total = await query.CountAsync(ct);
        var users = await query.Skip(skip).Take(take).ToListAsync(ct);

        var items = new List<AdminUserListItem>(users.Count);
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            items.Add(new AdminUserListItem(u.Id, u.Email ?? string.Empty, u.FullName, u.IsBlocked, roles.ToList(), u.CreatedAt));
        }
        return Ok(new AdminUserListResponse(total, items));
    }

    [HttpPost("{id:guid}/block")]
    [ProducesResponseType(typeof(AdminUserListItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserListItem>> Block(Guid id, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        user.IsBlocked = true;
        await userManager.UpdateAsync(user);
        await refreshStore.RevokeAllForUserAsync(id, ct);

        logger.LogInformation("User {UserId} blocked by admin {AdminId}", id, userManager.GetUserId(User));
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new AdminUserListItem(user.Id, user.Email ?? string.Empty, user.FullName, user.IsBlocked, roles.ToList(), user.CreatedAt));
    }

    [HttpPost("{id:guid}/unblock")]
    [ProducesResponseType(typeof(AdminUserListItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserListItem>> Unblock(Guid id, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        user.IsBlocked = false;
        await userManager.UpdateAsync(user);
        logger.LogInformation("User {UserId} unblocked by admin {AdminId}", id, userManager.GetUserId(User));
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new AdminUserListItem(user.Id, user.Email ?? string.Empty, user.FullName, user.IsBlocked, roles.ToList(), user.CreatedAt));
    }
}
