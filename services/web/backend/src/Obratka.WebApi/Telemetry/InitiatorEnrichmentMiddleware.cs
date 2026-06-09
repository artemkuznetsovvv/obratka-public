using Serilog.Context;

namespace Obratka.WebApi.Telemetry;

// Навешивает на Serilog LogContext «кто инициировал» текущий запрос (Initiator + UserId) — чтобы
// in-request логи (предупреждения/ошибки контроллеров и сервисов по ходу запроса) несли инициатора.
//
// ВАЖНО про порядок: регистрировать СТРОГО после app.UseAuthentication() — иначе context.User
// ещё не заполнен. Строку request-summary этот middleware НЕ покрывает (она пишется выше по стеку,
// после закрытия этого scope) — для неё инициатор кладётся через EnrichDiagnosticContext в Program.cs.
public sealed class InitiatorEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public InitiatorEnrichmentMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var initiator = InitiatorContext.Resolve(context.User);
        var userId = InitiatorContext.UserId(context.User);

        using (LogContext.PushProperty("Initiator", initiator))
        using (LogContext.PushProperty("UserId", userId))
        {
            await _next(context);
        }
    }
}
