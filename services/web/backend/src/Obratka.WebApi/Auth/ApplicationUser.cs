using Microsoft.AspNetCore.Identity;

namespace Obratka.WebApi.Auth;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsBlocked { get; set; }
    public string? TelegramChatId { get; set; }

    // Время последнего аутентифицированного запроса (обновляется LastActivityMiddleware с троттлингом).
    public DateTimeOffset? LastActivityAt { get; set; }
}
