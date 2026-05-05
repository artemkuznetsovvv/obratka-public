namespace ProcessingGateway.Domain;

/// Связь many-to-many `analysis_jobs` ↔ `reviews`. Решает проблему «какие отзывы
/// относятся к этому конкретному анализу-сессии»: повторный анализ той же компании
/// поглощает дубли через `ON CONFLICT DO NOTHING` на reviews, но в этой таблице
/// фиксируется участие отзыва в каждом job-е отдельной строкой.
///
/// `LlmDispatcher` SELECT-ит `reviews JOIN analysis_job_reviews WHERE analysis_job_id = J`,
/// получая ровно ту выборку, которая должна пойти в input.json.
public class AnalysisJobReview
{
    public Guid AnalysisJobId { get; set; }
    public long ReviewId { get; set; }

    public Review Review { get; set; } = null!;
}
