namespace Obratka.Modules.Analytics.Data.Entities;

// Read-only маппинг на processing.public.review_llm_results.
// overall_sentiment в schema 2.0 — русские строки 'позитивный'/'нейтральный'/'негативный'.
// aspects (jsonb) пока не маппим — добавим, когда будем делать метрики 3/5.
public sealed class ReviewLlmResult
{
    public long Id { get; init; }
    public long ReviewId { get; init; }
    public Guid AnalysisJobId { get; init; }
    public string OverallSentiment { get; init; } = string.Empty;
    public double OverallConfidence { get; init; }
}
