using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Telemetry;

// Обновляет ApplicationUser.LastActivityAt на аутентифицированных запросах (раздел «Пользователи»
// показывает «дата последней активности»). Чтобы не писать в БД на каждый запрос (фронт активно
// поллит), троттлим через IMemoryCache: не чаще раза в ThrottleMinutes на пользователя. Запись —
// одиночный UPDATE по PK через ExecuteUpdateAsync (без загрузки сущности). Регистрировать ПОСЛЕ
// app.UseAuthentication() (нужен заполненный User). Активность не должна ронять запрос → try/catch.
public sealed class LastActivityMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context)
    {
        await StampAsync(context);
        await next(context);
    }

    private async Task StampAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true) return;
        if (!Guid.TryParse(InitiatorContext.UserId(context.User), out var userId)) return;

        var key = $"lastact:{userId}";
        if (cache.TryGetValue(key, out _)) return; // в окне троттлинга — пропускаем запись
        // Ставим ключ ДО записи (дедуп конкурентных запросов в окне); при сбое — снимаем (см. catch),
        // чтобы неудачная запись не «съела» обновление на всё окно троттлинга.
        cache.Set(key, true, ThrottleWindow);

        try
        {
            var db = context.RequestServices.GetRequiredService<WebApiDbContext>();
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(u => u.LastActivityAt, DateTimeOffset.UtcNow),
                    context.RequestAborted);
        }
        catch (Exception ex)
        {
            // last-activity — не критично; не валим запрос. Снимаем throttle-ключ, чтобы сбой
            // не «съел» обновление на 5 минут (следующий запрос попробует снова).
            cache.Remove(key);
            context.RequestServices.GetService<ILogger<LastActivityMiddleware>>()?
                .LogWarning(ex, "LastActivity update failed for user {UserId}", userId);
        }
    }
}
