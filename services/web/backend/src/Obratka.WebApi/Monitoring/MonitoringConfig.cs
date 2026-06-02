namespace Obratka.WebApi.Monitoring;

// Конфигурация live-мониторинга компании. Модель «растущий seed-job»: мониторинг не создаёт
// новый analysis_job на цикл, а ДОПОЛНЯЕТ тот, что юзер выбрал при включении (SeedJobId) —
// каждый цикл добирает только новые отзывы и заново гонит весь набор через LLM.
// Скоупинг владельца — через Company.OwnerUserId (UserId дублируем для удобных выборок «мои»).
public class MonitoringConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }

    // Seed analysis_job — тот самый разовый анализ, который мониторинг «дополняет».
    public Guid SeedJobId { get; set; }

    // Снапшот выбора на момент включения. Sources — slug-и (2gis|yandex|google),
    // BranchIds — физические LogicalBranch.Id (reviews.branch_id).
    public List<string> Sources { get; set; } = new();
    public List<Guid> BranchIds { get; set; } = new();

    // Окно отображения в live-режиме (дни): 7 | 30 | 90.
    public int WindowDays { get; set; }

    public MonitoringFrequency Frequency { get; set; }
    public string CronSchedule { get; set; } = string.Empty;

    public MonitoringStatus Status { get; set; } = MonitoringStatus.Active;

    // Watermark: с какого момента добирать новые отзывы в следующем цикле (передаётся в dateFrom).
    public DateTimeOffset? LastCollectedAt { get; set; }
    public MonitoringCycleStatus? LastRunStatus { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MonitoringCycle> Cycles { get; set; } = new();
}
