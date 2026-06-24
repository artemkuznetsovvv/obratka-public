namespace ProcessingGateway.Domain;

/// Корневая запись pipeline анализа. PK = uuid — внешний идентификатор,
/// протекает через API/брокер/ключи S3/CorrelationId.
/// Поля и переходы статусов — ADR-004 §4 + CLAUDE.md (без `language_detection`).
///
/// Schema 2.0: `Recommendation` заменён на `Summary` + `RecommendationsCount`,
/// один `ResultUrl` разбит на `ResultReviewsUrl` + `ResultSummaryUrl`. Детальные рекомендации
/// живут в отдельной таблице `analysis_recommendations` (1:N к этой записи).
public class AnalysisJob
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    /// Снимок бизнес-контекста компании на момент запуска анализа (приходит в
    /// `StartAnalysisCommand` от Web API, который читает его из `Company` в `webapi_db` —
    /// PG в чужую БД не ходит). Прокидывается в `input.json` как доп. контекст для LLM.
    /// Опционально: старые/QA-запуски без контекста оставляют null.
    public string? BusinessCategory { get; set; }

    public string? BusinessSubcategory { get; set; }

    /// Свободный текст «Дополнительный контекст» из формы нового анализа (Company.Description).
    public string? AdditionalContext { get; set; }

    public AnalysisJobStatus Status { get; set; }

    public int ReviewCount { get; set; }

    /// JSONB: source slug → entry. Обновляется ParserPoller.
    public Dictionary<string, CollectionProgressEntry> CollectionProgress { get; set; } = new();

    /// `s3://obratka-jobs/{id}/input.json` после публикации в LLM.
    public string? PayloadUrl { get; set; }

    /// `s3://obratka-jobs/{id}/output_reviews.json` после ответа LLM.
    public string? ResultReviewsUrl { get; set; }

    /// `s3://obratka-jobs/{id}/output_summary.json` после ответа LLM.
    public string? ResultSummaryUrl { get; set; }

    /// Краткое резюме (1–3 предложения) от LLM. Используется в PDF-отчёте Web API.
    public string? Summary { get; set; }

    /// Денормализация для UI/быстрых запросов: `len(AnalysisRecommendations WHERE job=this)`.
    public int RecommendationsCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// Когда опубликовали в Llm__RequestQueue.
    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }
}
