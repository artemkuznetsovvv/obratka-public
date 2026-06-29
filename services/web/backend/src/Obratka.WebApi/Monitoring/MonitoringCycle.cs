namespace Obratka.WebApi.Monitoring;

// История одного цикла мониторинга — источник «изменений от цикла к циклу».
// PG хранит только АКТУАЛЬНОЕ состояние seed-job (разметку/summary/рекомендации перезаписывает
// каждый цикл); поэтому baseline для сравнения и снапшоты рекомендаций держим здесь, в webapi_db.
public class MonitoringCycle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitoringId { get; set; }

    // 0 = baseline-снапшот при включении мониторинга (без сбора); 1..N — реальные циклы.
    public int CycleNumber { get; set; }

    // StartedAt — wall-clock момент старта цикла. Это же граница окна для подсчёта «новых за цикл»
    // (reviews.collected_at >= StartedAt), поэтому фиксируем строго перед запуском сбора в PG.
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public MonitoringCycleStatus Status { get; set; } = MonitoringCycleStatus.Running;

    public DateTimeOffset? PeriodFrom { get; set; } // = LastCollectedAt на старте цикла
    public DateTimeOffset PeriodTo { get; set; }    // = StartedAt

    public int NewReviewCount { get; set; }
    public int TotalReviewsAtCycle { get; set; }

    // Доля «негативный» (в процентных пунктах, 0..100) по срезу новых отзывов цикла.
    public double NegativeRatioPp { get; set; }
    public bool NegativeSpikeTriggered { get; set; }

    public string? SummarySnapshot { get; set; }
    public List<RecommendationSnapshotItem> RecommendationsSnapshot { get; set; } = new();

    public string? Error { get; set; }
}

// Снимок одной рекомендации PG на момент цикла (зеркало analysis_recommendations).
public sealed class RecommendationSnapshotItem
{
    public int Priority { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ExpectedImpact { get; set; }
    public List<string> Evidence { get; set; } = new();
}
