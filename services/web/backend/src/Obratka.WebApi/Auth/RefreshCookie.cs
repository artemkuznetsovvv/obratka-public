using Microsoft.Extensions.Options;

namespace Obratka.WebApi.Auth;

public sealed class RefreshCookie(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public void Set(HttpResponse response, string token, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(_options.RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = _options.RefreshCookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = expiresAt,
            IsEssential = true
        });
    }

    public string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(_options.RefreshCookieName, out var v) ? v : null;

    public void Clear(HttpResponse response) =>
        response.Cookies.Delete(_options.RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = _options.RefreshCookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth"
        });
}
