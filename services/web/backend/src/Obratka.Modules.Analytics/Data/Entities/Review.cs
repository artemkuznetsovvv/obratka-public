namespace Obratka.Modules.Analytics.Data.Entities;

// Read-only маппинг на processing.public.reviews. Только поля, которые
// действительно нужны метрикам — расширяем по мере появления новых метрик.
// Заметка: одна запись reviews может входить в несколько analysis_jobs одной
// компании через analysis_job_reviews (M2M).
public sealed class Review
{
    public long Id { get; init; }
    public Guid CompanyId { get; init; }
    public Guid BranchId { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset ReviewDate { get; init; }
    public short? Stars { get; init; }
}
