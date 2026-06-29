using Hangfire.Dashboard;
using Obratka.WebApi.Auth;

namespace Obratka.WebApi.Scheduling;

// Доступ к /hangfire — только Admin (ADR-005 §«Hangfire Dashboard»).
//
// Caveat: основная аутентификация приложения — JWT Bearer, который браузер НЕ отправляет
// при обычном переходе на /hangfire. Поэтому:
//   • в Development открываем без проверки (удобно локально);
//   • в остальных средах требуем роль Admin в текущем HttpContext (наружу дашборд всё равно
//     закрыт nginx-ом / доступ через SSH-туннель — см. деплой PG/Parser).
// TODO(post-MVP): отдельная cookie-сессия для дашборда, чтобы работало из браузера в проде.
public sealed class HangfireDashboardAuthFilter(bool allowAnonymous) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        if (allowAnonymous)
            return true;

        var http = context.GetHttpContext();
        return http.User.Identity?.IsAuthenticated == true && http.User.IsInRole(Roles.Admin);
    }
}
