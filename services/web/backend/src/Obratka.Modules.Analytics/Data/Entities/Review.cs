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
    // raw_text — оригинальный текст отзыва. Используется в endpoint'ах
    // «топ примеров» (М3 раскрытие, опц. блок «Топ позитивных/негативных»).
    // Тяжёлое поле — НЕ Select'им в чисто-counts метриках, чтобы не тащить
    // лишние мегабайты.
    public string RawText { get; init; } = string.Empty;
}
