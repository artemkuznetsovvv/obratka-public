namespace Obratka.WebApi.Support;

// Анонимный запрос «Забыли пароль» (пользователь не залогинен).
public sealed record PasswordResetRequestRequest(string Email, string? Message);

public sealed record AdminUserRequestDto(
    Guid Id,
    string Type,        // passwordreset
    string Email,
    Guid? UserId,       // null, если email не совпал с аккаунтом
    string? Message,
    string Status,      // new | resolved
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record AdminUserRequestListResponse(
    int NewCount,
    IReadOnlyList<AdminUserRequestDto> Items);

// Admin: задать пользователю новый пароль вручную.
public sealed record AdminSetPasswordRequest(string NewPassword);
