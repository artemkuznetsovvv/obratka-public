namespace ProcessingGateway.Infrastructure.Parser;

/// Прокидывает `X-Correlation-ID` из текущего HttpContext в исходящие HTTP-запросы.
/// Корреляция уже лежит в `HttpContext.Items[X-Correlation-ID]` благодаря CorrelationIdMiddleware.
/// За пределами HTTP-запроса (например в MassTransit consumer) HttpContext == null;
/// тогда полагаемся на uplink-логику consumer-а проставить заголовок вручную через
/// `httpClient.DefaultRequestHeaders.Add(...)` per-вызов или через AsyncLocal-mediator.
/// Для MVP этого достаточно — на Этапе 5 при запуске consumer-ов добавлю явный pусher.
public sealed class CorrelationIdHandler : DelegatingHandler
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly IHttpContextAccessor _accessor;

    public CorrelationIdHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(HeaderName))
        {
            var ctx = _accessor.HttpContext;
            if (ctx is not null && ctx.Items.TryGetValue(HeaderName, out var value) && value is string id)
            {
                request.Headers.TryAddWithoutValidation(HeaderName, id);
            }
        }
        return base.SendAsync(request, cancellationToken);
    }
}
