using System.ComponentModel.DataAnnotations;

namespace Obratka.WebApi.Contracts.Auth;

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    [Required, MaxLength(200)] string FullName);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    UserInfo User);

public sealed record UserInfo(
    Guid Id,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles);

// Self-service: пользователь меняет своё ФИО и/или email на странице профиля.
public sealed record UpdateProfileRequest(
    [Required, MaxLength(200)] string FullName,
    [Required, EmailAddress] string Email);

// Self-service смена пароля: нужно знать текущий пароль.
public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);
