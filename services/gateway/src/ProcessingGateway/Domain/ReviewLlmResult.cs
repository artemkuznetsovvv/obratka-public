namespace ProcessingGateway.Domain;

/// Per-review результат LLM (schema 2.0). PK = bigint identity.
/// FK `review_id` — bigint на `reviews.id`.
///
/// Изменения относительно 1.0:
/// - удалены `FakeStatus`, `FakeReasonTags`, `IsSpam`, `SpamConfidence`, `Topics`
/// - добавлены `OverallSentiment` (русский enum), `OverallConfidence`, `Aspects` (JSONB)
public class ReviewLlmResult
{
    public long Id { get; set; }

    public long ReviewId { get; set; }
    public Review Review { get; set; } = null!;

    public Guid AnalysisJobId { get; set; }

    /// "позитивный" | "негативный" | "нейтральный" — закрытый русский enum от LLM-сервиса.
    public required string OverallSentiment { get; set; }

    /// 0.0..1.0. При `0.0` обычно `Aspects` пустой — модель не нашла уверенного сигнала.
    public double OverallConfidence { get; set; }

    /// JSONB + GIN-индекс для фильтра «отзывы с темой X / тональностью Y».
    /// Один и тот же `Topic` может встречаться **несколько раз** с разной тональностью.
    public List<ReviewAspect> Aspects { get; set; } = new();

    public DateTimeOffset ProcessedAt { get; set; }
}

/// Один aspect-объект внутри `ReviewLlmResult.Aspects`.
public class ReviewAspect
{
    public string Topic { get; set; } = "";
    public string Sentiment { get; set; } = "";
    public double Confidence { get; set; }

    /// Цитата из текста отзыва. Может быть пустой строкой.
    public string Fragment { get; set; } = "";

    /// `true` если `Topic` не из закрытого справочника тем.
    public bool IsFreeform { get; set; }
}
