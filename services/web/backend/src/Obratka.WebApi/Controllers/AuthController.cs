using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Support;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IJwtTokenService jwt,
    IRefreshTokenStore refreshStore,
    RefreshCookie refreshCookie,
    WebApiDbContext db,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            return BadRequest(new { error = "Email already registered" });

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var create = await userManager.CreateAsync(user, request.Password);
        if (!create.Succeeded)
            return BadRequest(new { error = string.Join("; ", create.Errors.Select(e => e.Description)) });

        await userManager.AddToRoleAsync(user, Roles.User);
        return await IssueTokensAsync(user, ct);
    }

    // «Забыли пароль» без email-флоу: фиксируем обращение в борду админки.
    // Админ меняет пароль вручную (см. AdminUsersController.SetPassword) и уведомляет пользователя.
    // Анонимно: пользователь не залогинен. Всегда 200 — не палим, существует ли аккаунт.
    [HttpPost("password-reset-request")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PasswordResetRequest(
        [FromBody] PasswordResetRequestRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Введите email" });

        var user = await userManager.FindByEmailAsync(email);

        // Анти-спам/идемпотентность: не плодим дубли, пока есть необработанный запрос с этого email.
        var hasPending = await db.UserRequests.AnyAsync(r =>
            r.Type == UserRequestType.PasswordReset
            && r.Status == UserRequestStatus.New
            && r.Email == email, ct);

        if (!hasPending)
        {
            db.UserRequests.Add(new UserRequest
            {
                Type = UserRequestType.PasswordReset,
                Email = email,
                UserId = user?.Id,
                Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
                Status = UserRequestStatus.New,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Password-reset request recorded for {Email} (userFound={Found})", email, user is not null);
        }

        return Ok(new { ok = true });
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized(new { error = "Invalid email or password" });

        if (user.IsBlocked)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "User is blocked" });

        var check = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            if (check.IsLockedOut)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "User is locked out" });
            return Unauthorized(new { error = "Invalid email or password" });
        }

        return await IssueTokensAsync(user, ct);
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        var rawToken = refreshCookie.Read(Request);
        if (string.IsNullOrEmpty(rawToken))
            return Unauthorized(new { error = "Refresh token missing" });

        var entity = await refreshStore.FindActiveAsync(rawToken, ct);
        if (entity is null)
        {
            refreshCookie.Clear(Response);
            return Unauthorized(new { error = "Refresh token invalid or expired" });
        }

        var user = await userManager.FindByIdAsync(entity.UserId.ToString());
        if (user is null || user.IsBlocked)
        {
            await refreshStore.RevokeAsync(rawToken, ct);
            refreshCookie.Clear(Response);
            return Unauthorized(new { error = "User no longer valid" });
        }

        // rotate refresh token
        await refreshStore.RevokeAsync(rawToken, ct);
        return await IssueTokensAsync(user, ct);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var rawToken = refreshCookie.Read(Request);
        if (!string.IsNullOrEmpty(rawToken))
            await refreshStore.RevokeAsync(rawToken, ct);
        refreshCookie.Clear(Response);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserInfo>> Me()
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new UserInfo(user.Id, user.Email ?? string.Empty, user.FullName, roles.ToList()));
    }

    // Self-service: смена ФИО и/или email на странице профиля.
    [HttpPut("profile")]
    [Authorize]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserInfo>> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var newFullName = request.FullName.Trim();
        var newEmail = request.Email.Trim();
        if (string.IsNullOrWhiteSpace(newFullName))
            return BadRequest(new { error = "Введите имя" });

        // Email сменился — проверяем уникальность и обновляем заодно UserName (= email).
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

        user.FullName = newFullName;
        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded)
            return BadRequest(new { error = string.Join("; ", update.Errors.Select(e => e.Description)) });

        logger.LogInformation("User {UserId} updated own profile", userId);
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new UserInfo(user.Id, user.Email ?? string.Empty, user.FullName, roles.ToList()));
    }

    // Self-service: смена собственного пароля (нужен текущий). JWT остаётся валиден до
    // истечения — текущую сессию не разлогиниваем (пользователь сам сменил пароль).
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = userManager.GetUserId(User);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });

        logger.LogInformation("User {UserId} changed own password", userId);
        return NoContent();
    }

    private async Task<ActionResult<AuthResponse>> IssueTokensAsync(ApplicationUser user, CancellationToken ct)
    {
        var roles = await userManager.GetRolesAsync(user);
        var access = jwt.GenerateAccessToken(user, roles);
        var refresh = await refreshStore.IssueAsync(user.Id, ct);
        refreshCookie.Set(Response, refresh.Token, refresh.ExpiresAt);
        logger.LogInformation("Issued tokens for {UserId} ({Email})", user.Id, user.Email);

        return Ok(new AuthResponse(
            access.Token,
            access.ExpiresAt,
            new UserInfo(user.Id, user.Email ?? string.Empty, user.FullName, roles.ToList())));
    }
}
