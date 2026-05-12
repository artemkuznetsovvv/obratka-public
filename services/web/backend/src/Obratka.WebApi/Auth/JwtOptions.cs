namespace Obratka.WebApi.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Secret { get; init; } = string.Empty;
    public string Issuer { get; init; } = "obratka-api";
    public string Audience { get; init; } = "obratka-spa";
    public int AccessTokenExpiryMinutes { get; init; } = 15;
    public int RefreshTokenExpiryDays { get; init; } = 7;
    public string RefreshCookieName { get; init; } = "__obratka_rt";
    public bool RefreshCookieSecure { get; init; } = true;
}
