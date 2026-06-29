namespace ProcessingGateway.Domain;

/// Job-level рекомендация от LLM (schema 2.0). 1:N к `AnalysisJob`.
/// Соответствует одному элементу `output_summary.full_recommendations[]`.
///
/// При replay от LLM (повторный приход того же `LlmResultMessage`) — таблица
/// **очищается по `analysis_job_id` и заполняется заново** (DELETE+INSERT).
/// Это проще, чем upsert по содержимому, и идемпотентно.
public class AnalysisRecommendation
{
    public long Id { get; set; }

    public Guid AnalysisJobId { get; set; }
    public AnalysisJob AnalysisJob { get; set; } = null!;

    /// 1 = критично (безопасность, репутация, повторные визиты),
    /// 2 = важно (UX, конверсия, ожидание),
    /// 3 = полезно (маркетинг, удержание, операционные).
    public short Priority { get; set; }

    public required string Topic { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }

    public string? ExpectedImpact { get; set; }

    /// JSONB: массив строк (цитаты или ссылки на review_id).
    public List<string> Evidence { get; set; } = new();

    /// Порядок из `full_recommendations[]` (для стабильного рендера на UI).
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
