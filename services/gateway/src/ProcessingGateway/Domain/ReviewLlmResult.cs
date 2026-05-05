namespace ProcessingGateway.Domain;

/// Результат LLM per review. Схема — ADR-002 §«Схема таблиц». PK = bigint identity
/// (см. отклонение в Review.cs). FK `review_id` — bigint на `reviews.id`.
public class ReviewLlmResult
{
    public long Id { get; set; }

    public long ReviewId { get; set; }
    public Review Review { get; set; } = null!;

    public Guid AnalysisJobId { get; set; }

    /// "normal" | "suspicious" | "fake" — slug-формат от LLM.
    public required string FakeStatus { get; set; }

    /// JSONB, например ["однотипный текст", "массовая публикация"].
    public List<string> FakeReasonTags { get; set; } = new();

    /// "very_negative" | "negative" | "neutral" | "positive" | "very_positive". null при неуверенности LLM.
    public string? Sentiment { get; set; }

    public double? SentimentConfidence { get; set; }

    public bool IsSpam { get; set; }
    public double SpamConfidence { get; set; }

    /// JSONB + GIN-индекс для фильтра «отзывы по теме» в Web API Analytics.
    public List<string> Topics { get; set; } = new();

    public DateTimeOffset ProcessedAt { get; set; }
}
