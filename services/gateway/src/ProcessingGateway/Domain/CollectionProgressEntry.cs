namespace ProcessingGateway.Domain;

/// Один источник в `analysis_jobs.collection_progress` (JSONB-словарь
/// source slug → entry). Обновляется ParserPoller-ом на Этапе 5.
public record CollectionProgressEntry
{
    public Guid? TaskId { get; init; }

    /// Момент старта **этой** Parser-таски. Используется для расчёта таймаута per-source,
    /// чтобы рестарт источника на старом job-е не помечался failed сразу же
    /// (для legacy-job-ов без этого поля поллер фолбэчится на `analysis_jobs.created_at`).
    public DateTimeOffset? StartedAt { get; init; }

    /// "pending" | "running" | "completed" | "failed" — slug-формат Parser-а.
    public required string Status { get; init; }

    /// 0..100 (а не 0..1, как у Parser-а — мы умножаем при ингесте, чтобы фронту был
    /// единый формат, см. CLAUDE.md HTTP Status API).
    public int Progress { get; init; }

    public int? ReviewCount { get; init; }

    public string? S3Url { get; init; }

    public string? Error { get; init; }
}
