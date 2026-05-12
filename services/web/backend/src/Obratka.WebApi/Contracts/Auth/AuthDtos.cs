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
