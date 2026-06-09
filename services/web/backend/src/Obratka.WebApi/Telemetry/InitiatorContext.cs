using System.Security.Claims;
using Obratka.WebApi.Auth;

namespace Obratka.WebApi.Telemetry;

// «Кто инициировал» запрос (ADR-008). Web API — единственный сервис, знающий про пользователей;
// PG/Parser всегда system:* (граница по CLAUDE.md). Связь system-логов с человеком — через
// общий AnalysisJobId.
//
// userId читаем из ClaimTypes.NameIdentifier (JWT sub маппится в него при дефолтном
// MapInboundClaims) — тот же claim, что и userManager.GetUserId(User) в контроллерах.
public static class InitiatorContext
{
    public static string? UserId(ClaimsPrincipal user)
        => user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;

    // user:<guid> | admin:<guid> | anonymous
    public static string Resolve(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return "anonymous";
        var id = UserId(user);
        return user.IsInRole(Roles.Admin) ? $"admin:{id}" : $"user:{id}";
    }
}
