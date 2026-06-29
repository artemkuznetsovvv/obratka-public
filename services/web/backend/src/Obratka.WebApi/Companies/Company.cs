namespace Obratka.WebApi.Companies;

public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public List<string> Cities { get; set; } = new();
    public string? Description { get; set; }

    // Доп. Telegram chat_id, куда дублируются РЕЗУЛЬТАТЫ анализов этой компании (live-мониторинг
    // и разовые) — сверх личного чата владельца. Настраивается админом в админ-панели. На ошибки
    // не влияет (те идут на Telegram:AdminChatIds).
    public List<string> NotificationChatIds { get; set; } = new();

    // «Настройки следующего анализа» — позволяют юзеру вернуться в воронку
    // через несколько дней. На шаге 1 мастера эти поля заполняются и persist'ятся
    // здесь; sessionStorage используется как кэш в пределах одной сессии (для скорости),
    // но БД — источник истины для cross-session continuity. Не очищаются после запуска:
    // могут быть default'ом для следующего анализа той же компании.
    public DateTimeOffset? DraftPeriodFrom { get; set; }
    public DateTimeOffset? DraftPeriodTo { get; set; }
    public List<string>? DraftSources { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CompanyBranch> Branches { get; set; } = new();
}
