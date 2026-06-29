namespace Obratka.Modules.Analytics.Data.Entities;

// M2M между analysis_jobs и reviews. Нужен, чтобы понять какие reviews
// относятся к конкретному job-у (reviews шарятся между job'ами одной компании).
public sealed class AnalysisJobReview
{
    public Guid AnalysisJobId { get; init; }
    public long ReviewId { get; init; }
}
