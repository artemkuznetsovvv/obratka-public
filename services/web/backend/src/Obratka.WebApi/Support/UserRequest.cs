namespace Obratka.WebApi.Support;

// Тип обращения пользователя. Пока только сброс пароля; задел под «борду запросов»
// (можно добавлять другие типы — вопрос, баг-репорт и т.п.).
public enum UserRequestType
{
    PasswordReset,
}

public enum UserRequestStatus
{
    New,
    Resolved,
}

// Обращение пользователя в поддержку/админку. Для «Забыли пароль» создаётся анонимно
// (пользователь не залогинен) по введённому email; UserId резолвится, если email совпал
// с существующим аккаунтом. Админ видит борду таких запросов и обрабатывает вручную
// (меняет пароль + сообщает пользователю).
public class UserRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public UserRequestType Type { get; set; }
    public string Email { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? Message { get; set; }
    public UserRequestStatus Status { get; set; } = UserRequestStatus.New;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
}
