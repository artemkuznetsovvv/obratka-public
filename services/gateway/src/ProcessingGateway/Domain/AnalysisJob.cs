namespace ProcessingGateway.Domain;

/// Корневая запись pipeline анализа. PK = uuid — это **внешний идентификатор**,
/// протекает через API/брокер/ключи S3/CorrelationId.
/// Поля и переходы статусов — ADR-004 §4 + CLAUDE.md (без `language_detection`).
public class AnalysisJob
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public AnalysisJobStatus Status { get; set; }

    public int ReviewCount { get; set; }

    /// JSONB: source slug → entry. Обновляется ParserPoller на Этапе 5.
    public Dictionary<string, CollectionProgressEntry> CollectionProgress { get; set; } = new();

    /// `s3://obratka-jobs/{id}/input.json` после публикации в LLM.
    public string? PayloadUrl { get; set; }

    /// `s3://obratka-jobs/{id}/output.json` после получения ответа LLM.
    public string? ResultUrl { get; set; }

    /// Job-level recommendation от LLM (НЕ per-review, ADR-004 §2).
    public string? Recommendation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// Когда опубликовали в Llm__RequestQueue.
    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }
}
