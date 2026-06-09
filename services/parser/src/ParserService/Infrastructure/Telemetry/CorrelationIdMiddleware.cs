using Serilog.Context;

namespace ParserService.Infrastructure.Telemetry;

// Ловит X-Correlation-ID, который Processing Gateway шлёт на POST /api/collection-tasks и
// GET /api/collection-tasks/{taskId} (иначе генерит новый Guid "N"), кладёт обратно в заголовок
// ответа + HttpContext.Items и пушит в Serilog LogContext, чтобы все логи запроса несли
// CorrelationId. Зеркало Processing Gateway / Web API CorrelationIdMiddleware — единый контракт.
//
// ВАЖНО: фоновый сбор (CollectionTaskBackgroundService → CollectionTaskOrchestrator) идёт ВНЕ
// этого HTTP-запроса (через in-memory TaskQueue), и сюда X-Correlation-ID не доходит. Там
// сквозной трейс восстанавливается по AnalysisJobId(=CollectionTask.JobId) из БД, см. оркестратор.
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
