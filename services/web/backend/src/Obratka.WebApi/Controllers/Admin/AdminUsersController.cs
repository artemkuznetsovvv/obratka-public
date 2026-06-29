using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Admin;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Monitoring;
using Obratka.WebApi.Scheduling;
using Obratka.WebApi.Support;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/users")]
public sealed class AdminUsersController(
    UserManager<ApplicationUser> userManager,
    WebApiDbContext db,
    IRefreshTokenStore refreshStore,
    IProcessingGatewayClient gateway,
    IMonitoringScheduler scheduler,
    ILogger<AdminUsersController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AdminUserListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminUserListResponse>> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(offset, 0);

        var query = db.Users.AsNoTracking().AsQueryable();

        // Поиск: email / имя (ILike), либо точный ID (если term парсится в Guid).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = $"%{term}%";
            if (Guid.TryParse(term, out var gid))
                query = query.Where(u => u.Id == gid
                    || (u.Email != null && EF.Functions.ILike(u.Email, pattern))
                    || EF.Functions.ILike(u.FullName, pattern));
            else
                query = query.Where(u =>
                    (u.Email != null && EF.Functions.ILike(u.Email, pattern))
                    || EF.Functions.ILike(u.FullName, pattern));
        }

        // Фильтр по статусу.
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            query = query.Where(u => !u.IsBlocked);
        else if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
            query = query.Where(u => u.IsBlocked);

        var ordered = query.OrderByDescending(u => u.CreatedAt);
        var total = await ordered.CountAsync(ct);

        // Счётчик компаний — коррелированным подзапросом (страница маленькая, без N+1 по БД).
        var page = await ordered
            .Skip(skip).Take(take)
            .Select(u => new
            {
                User = u,
                CompaniesCount = db.Companies.Count(c => c.OwnerUserId == u.Id),
            })
            .ToListAsync(ct);

        var items = new List<AdminUserListItem>(page.Count);
        foreach (var row in page)
        {
            var roles = await userManager.GetRolesAsync(row.User);
            items.Add(new AdminUserListItem(
                row.User.Id, row.User.Email ?? string.Empty, row.User.FullName,
                row.User.IsBlocked, roles.ToList(), row.User.CreatedAt,
                row.CompaniesCount, row.User.LastActivityAt));
        }
        return Ok(new AdminUserListResponse(total, items));
    }

    // Карточка пользователя: основная инфа + его компании (с агрегатами).
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminUserDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetails>> Get(Guid id, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        var roles = await userManager.GetRolesAsync(user);

        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.OwnerUserId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.CreatedAt,
                Sources = c.DraftSources,
                BranchCount = db.LogicalBranches.Count(lb => lb.CompanyId == c.Id),
                HasActiveMonitoring = db.MonitoringConfigs
                    .Any(m => m.CompanyId == c.Id && m.Status == MonitoringStatus.Active),
            })
            .ToListAsync(ct);

        // Кол-во анализов per company — из PG (analytics_reader не маппит analysis_jobs).
        // Параллельно; PG-сбой деградирует в null («—»), не валит карточку.
        var counts = await Task.WhenAll(companies.Select(async c =>
        {
            try
            {
                var resp = await gateway.ListAnalysesAsync(null, c.Id, 1, 0, ct);
                return (c.Id, Count: (int?)resp.Total);
            }
            catch (Exception ex) when (ex is ProcessingGatewayException or HttpRequestException)
            {
                logger.LogWarning(ex, "PG analyses count failed for company {CompanyId}", c.Id);
                return (c.Id, Count: (int?)null);
            }
        }));
        var countById = counts.ToDictionary(x => x.Id, x => x.Count);

        var companyDtos = companies.Select(c => new AdminUserCompanyDto(
            c.Id, c.Name, c.CreatedAt,
            (IReadOnlyList<string>)(c.Sources ?? new List<string>()),
            c.BranchCount,
            countById.TryGetValue(c.Id, out var cnt) ? cnt : null,
            c.HasActiveMonitoring)).ToList();

        return Ok(new AdminUserDetails(
            user.Id, user.Email ?? string.Empty, user.FullName, user.PhoneNumber,
            user.CreatedAt, user.IsBlocked, user.LastActivityAt, roles.ToList(), companyDtos));
    }

    // Редактирование email / имени / телефона (пароль — отдельным set-password).
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(AdminUserDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetails>> Update(
        Guid id, [FromBody] AdminUpdateUserRequest request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();

        var newEmail = request.Email?.Trim() ?? string.Empty;
        var newFullName = request.FullName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newEmail)) return BadRequest(new { error = "Введите email" });
        if (string.IsNullOrWhiteSpace(newFullName)) return BadRequest(new { error = "Введите имя" });

        // Email сменился — уникальность + синхронизация UserName (= email), как в self-service.
        if (!string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await userManager.FindByEmailAsync(newEmail);
            if (existing is not null && existing.Id != user.Id)
                return BadRequest(new { error = "Этот email уже занят" });

            var setEmail = await userManager.SetEmailAsync(user, newEmail);
            if (!setEmail.Succeeded)
                return BadRequest(new { error = string.Join("; ", setEmail.Errors.Select(e => e.Description)) });
            var setUserName = await userManager.SetUserNameAsync(user, newEmail);
            if (!setUserName.Succeeded)
                return BadRequest(new { error = string.Join("; ", setUserName.Errors.Select(e => e.Description)) });
        }

        var phone = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
        if (!string.Equals(phone, user.PhoneNumber, StringComparison.Ordinal))
        {
            var setPhone = await userManager.SetPhoneNumberAsync(user, phone);
            if (!setPhone.Succeeded)
                return BadRequest(new { error = string.Join("; ", setPhone.Errors.Select(e => e.Description)) });
        }

        user.FullName = newFullName;
        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded)
            return BadRequest(new { error = string.Join("; ", update.Errors.Select(e => e.Description)) });

        logger.LogInformation("User {UserId} edited by admin {AdminId}", id, userManager.GetUserId(User));
        return await Get(id, ct);
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

        // ТЗ §5: блокировка ставит мониторинги пользователя на паузу (новые циклы не запускаются).
        // Разблокировка НЕ возобновляет автоматически — пользователь перезапускает сам.
        var configs = await db.MonitoringConfigs
            .Where(m => m.UserId == id && m.Status == MonitoringStatus.Active)
            .ToListAsync(ct);
        foreach (var m in configs)
        {
            m.Status = MonitoringStatus.Paused;
            m.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (configs.Count > 0) await db.SaveChangesAsync(ct);
        // Снятие Hangfire-джобы — best-effort: даже если планировщик недоступен, статус Paused в БД
        // уже не даст циклу запуститься (MonitoringCycleRunner пропускает не-Active), блокировку не валим.
        foreach (var m in configs)
        {
            try { scheduler.Remove(m.Id); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to remove Hangfire job for monitoring {Id} on block", m.Id); }
        }

        logger.LogInformation("User {UserId} blocked by admin {AdminId} ({Paused} monitorings paused)",
            id, userManager.GetUserId(User), configs.Count);
        return Ok(await ToListItemAsync(user, ct));
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
        return Ok(await ToListItemAsync(user, ct));
    }

    // Ручная смена пароля пользователю админом (флоу «Сбросить пароль» без email).
    // Сбрасываем активные refresh-токены — пользователь перелогинится с новым паролем.
    [HttpPost("{id:guid}/set-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] AdminSetPasswordRequest request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new { error = "Введите новый пароль" });

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });

        await refreshStore.RevokeAllForUserAsync(id, ct);
        logger.LogInformation("Password set for user {UserId} by admin {AdminId}", id, userManager.GetUserId(User));
        return NoContent();
    }

    private async Task<AdminUserListItem> ToListItemAsync(ApplicationUser user, CancellationToken ct)
    {
        var roles = await userManager.GetRolesAsync(user);
        var companiesCount = await db.Companies.CountAsync(c => c.OwnerUserId == user.Id, ct);
        return new AdminUserListItem(
            user.Id, user.Email ?? string.Empty, user.FullName, user.IsBlocked,
            roles.ToList(), user.CreatedAt, companiesCount, user.LastActivityAt);
    }
}
