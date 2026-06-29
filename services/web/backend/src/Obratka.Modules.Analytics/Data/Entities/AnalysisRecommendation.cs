namespace Obratka.Modules.Analytics.Data.Entities;

// Read-only маппинг на processing.public.analysis_recommendations.
// LLM-генерированные рекомендации по результатам анализа. Один analysis_job →
// 0..N рекомендаций. Порядок отображения — sort_order ASC.
//
// priority: 1..3 (1 = высокий, 3 = низкий) — определяется LLM-пайплайном.
// evidence: jsonb-массив цитат из отзывов, подтверждающих рекомендацию.
public sealed class AnalysisRecommendation
{
    public long Id { get; init; }
    public Guid AnalysisJobId { get; init; }
    public short Priority { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string? ExpectedImpact { get; init; }
    public List<string> Evidence { get; init; } = new();
    public int SortOrder { get; init; }
}
