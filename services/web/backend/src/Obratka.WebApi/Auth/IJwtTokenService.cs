namespace Obratka.WebApi.Auth;

public interface IJwtTokenService
{
    AccessToken GenerateAccessToken(ApplicationUser user, IEnumerable<string> roles);
    string GenerateRefreshTokenValue();
}

public readonly record struct AccessToken(string Token, DateTimeOffset ExpiresAt);
