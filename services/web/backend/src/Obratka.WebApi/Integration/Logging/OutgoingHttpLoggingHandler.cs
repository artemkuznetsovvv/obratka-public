using System.Diagnostics;
using Obratka.WebApi.Telemetry;

namespace Obratka.WebApi.Integration.Logging;

// DelegatingHandler для исходящих вызовов к downstream-сервисам (Parser / Processing Gateway):
//   1) прокидывает X-Correlation-ID из текущего HttpContext дальше — сквозная корреляция запроса;
//   2) логирует каждый вызов структурно: Direction=outgoing, TargetService, метод, путь, StatusCode,
//      ElapsedMs — это «куда был запрос / результат / длительность» из ТЗ.
//
// Вне HTTP-контекста (Hangfire/фон, HttpContext == null) генерим correlation, чтобы цепочка
// исходящих вызовов из джобы всё равно имела общий id. Рутинные 2xx — на Debug (в проде с
// MinimumLevel=Information не шумят; поллинг PG раз в 3-5с не засоряет лог).
//
// Регистрируется per-client с именем сервиса (см. Program.cs AddHttpMessageHandler(sp => new ...)).
internal sealed class OutgoingHttpLoggingHandler : DelegatingHandler
{
    private readonly string _targetService;
    private readonly IHttpContextAccessor _accessor;
    private readonly ILogger<OutgoingHttpLoggingHandler> _logger;

    public OutgoingHttpLoggingHandler(
        string targetService,
        IHttpContextAccessor accessor,
        ILogger<OutgoingHttpLoggingHandler> logger)
    {
        _targetService = targetService;
        _accessor = accessor;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            var ctx = _accessor.HttpContext;
            var correlationId =
                ctx is not null
                && ctx.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var v)
                && v is string s
                    ? s
                    : Guid.NewGuid().ToString("N");
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        var path = request.RequestUri?.PathAndQuery;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct);
            sw.Stop();
            var code = (int)response.StatusCode;
            var level = code >= 500 ? LogLevel.Error
                : code >= 400 ? LogLevel.Warning
                : sw.ElapsedMilliseconds > 1000 ? LogLevel.Warning
                : LogLevel.Debug;
            _logger.Log(level,
                "{Direction} {HttpMethod} {TargetService} {RequestPath} -> {StatusCode} in {ElapsedMs}ms",
                "outgoing", request.Method.Method, _targetService, path, code, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "{Direction} {HttpMethod} {TargetService} {RequestPath} failed in {ElapsedMs}ms",
                "outgoing", request.Method.Method, _targetService, path, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
