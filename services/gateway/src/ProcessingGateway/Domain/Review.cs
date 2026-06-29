namespace ProcessingGateway.Domain;

/// Сырой отзыв, ингестируемый из `s3://obratka-jobs/{jobId}/raw/{source}.json` в `reviews`.
/// Схема — ADR-002 + дополнительные поля из Parser RawReview (text_language, author_name,
/// author_public_id), см. CLAUDE.md.
///
/// Отклонение от ADR-002: PK = `bigint identity` (а не uuid). Внутренняя массовая таблица,
/// 8 байт vs 16, sequential B-tree плотнее. Внешние идентификаторы (`company_id`, `branch_id`,
/// `analysis_job_id` в `review_llm_results`) остаются UUID.
public class Review
{
    public long Id { get; set; }

    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }

    /// slug: "2gis" | "yandex" | "google" | "otzovik". C#-enum-имена за пределами кода не светят.
    public required string Source { get; set; }

    public string? ExternalId { get; set; }

    /// Простая детерминированная схема (решение Этапа 0 №8 + Этап 3).
    /// `{source}:{branchId:N}:ext:{externalId}` либо `{source}:{branchId:N}:dt:{unix}:{first 200 chars}`.
    public required string CompositeKey { get; set; }

    public required string RawText { get; set; }

    /// Заполняется LLM (если когда-то понадобится). В MVP — null.
    public string? NormalizedText { get; set; }

    /// Из `RawReview.TextLanguage` Parser-а.
    public string? TextLanguage { get; set; }

    public DateTimeOffset ReviewDate { get; set; }

    public short? Stars { get; set; }

    public string? AuthorName { get; set; }
    public string? AuthorPublicId { get; set; }

    public DateTimeOffset CollectedAt { get; set; }
}
