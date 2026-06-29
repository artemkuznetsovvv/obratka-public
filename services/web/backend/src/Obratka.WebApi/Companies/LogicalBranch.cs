namespace Obratka.WebApi.Companies;

// Логический («физический») филиал — пользовательская сущность «одна точка моего бизнеса».
// Объединяет несколько CompanyBranch (карточек на 2ГИС/Яндекс/Google) одной точки.
// Создаётся на шаге выбора филиалов: автогруппировкой + ручными правками юзера.
public class LogicalBranch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    // Каноническое название, обычно самое полное из найденных. Юзер может переопределить.
    public string Name { get; set; } = string.Empty;
    // Каноническая запись адреса (одна из источниковых, выбранная как «лучшая»).
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    // Опц. координаты — заполнятся когда парсер начнёт их отдавать; используется
    // в алгоритме автогруппировки как третий критерий.
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    // Главный чекбокс блока «Включить филиал в анализ». False = филиал целиком исключён.
    public bool IsSelected { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CompanyBranch> Cards { get; set; } = new();
}
