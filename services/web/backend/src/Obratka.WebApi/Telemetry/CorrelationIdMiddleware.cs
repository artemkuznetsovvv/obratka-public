using Serilog.Context;

namespace Obratka.WebApi.Telemetry;

// Единый id цепочки одного HTTP-запроса (ADR-008). Читает X-Correlation-ID из входящего запроса
// (от фронта/вышестоящего сервиса) либо генерит новый Guid "N"; кладёт обратно в заголовок ответа
// и в HttpContext.Items (откуда исходящие хендлеры прокидывают его дальше — в Parser/PG), и пушит
// в Serilog LogContext, чтобы ВСЕ логи запроса несли CorrelationId.
//
// ВАЖНО про порядок: регистрировать СТРОГО до app.UseSerilogRequestLogging() — иначе строка
// request-summary (она пишется уже после возврата из downstream) не увидит CorrelationId, т.к.
// using-scope downstream-middleware к тому моменту будет закрыт.
//
// Зеркало ProcessingGateway.Infrastructure.Telemetry.CorrelationIdMiddleware — единый контракт
// заголовка и имени свойства между сервисами.
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = correlationId;
        context.Items[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
