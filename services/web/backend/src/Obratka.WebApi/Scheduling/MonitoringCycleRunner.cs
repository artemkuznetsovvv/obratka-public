using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obratka.Modules.Analytics.Monitoring;
using Obratka.Modules.Analytics.Recommendations;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;
using Obratka.WebApi.Monitoring;

namespace Obratka.WebApi.Scheduling;

// Драйвер цикла live-мониторинга («растущий seed-job»).
// TriggerAsync — стартует один цикл: проверки сериализации → создаёт MonitoringCycle(running) →
//   restart-source per источник в PG с dateFrom=lastCollectedAt (PG авто-доезжает: сбор → re-LLM
//   на всём наборе → overwrite summary/recs). НЕ ждём — финализирует ReconcilePendingAsync.
// ReconcilePendingAsync — глобальный recurring: добивает циклы, чьи seed-job в PG достигли
//   терминального статуса (считает new-count/негатив, снимает снапшот рекомендаций, правило спайка,
//   двигает watermark, шлёт уведомления).
//
// Analytics-сервисы (stats/recs) опциональны: при пустом ProcessingReadDb они не зарегистрированы
// (как у метрик — 503). Регистрируем рантайм через ActivatorUtilities, чтобы default null сработал.
internal sealed class MonitoringCycleRunner(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    INotificationsModule notifications,
    IOptions<MonitoringOptions> options,
    ILogger<MonitoringCycleRunner> logger,
    IMonitoringStatsService? stats = null,
    IRecommendationsService? recommendations = null) : IMonitoringCycleRunner
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public async Task TriggerAsync(Guid monitoringId)
    {
        var config = await db.MonitoringConfigs.FirstOrDefaultAsync(m => m.Id == monitoringId, Ct);
        if (config is null)
        {
            logger.LogWarning("Monitoring {Id} not found — skip trigger", monitoringId);
            return;
        }
        if (config.Status != MonitoringStatus.Active)
        {
            logger.LogInformation("Monitoring {Id} status {Status} — skip trigger", monitoringId, config.Status);
            return;
        }

        // Сериализация: не запускаем новый цикл, пока предыдущий открыт.
        var hasOpen = await db.MonitoringCycles
            .AnyAsync(c => c.MonitoringId == monitoringId && c.FinishedAt == null, Ct);
        if (hasOpen)
        {
            logger.LogInformation("Monitoring {Id} has an open cycle — skip trigger", monitoringId);
            return;
        }

        AnalysisJobDto? job;
        try
        {
            job = await gateway.GetAnalysisAsync(config.SeedJobId, Ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Monitoring {Id}: PG GetAnalysis failed — skip trigger", monitoringId);
            return;
        }
        if (job is null)
        {
            logger.LogWarning("Monitoring {Id}: seed job {JobId} not found — skip trigger",
                monitoringId, config.SeedJobId);
            return;
        }
        if (!IsTerminal(job.Status))
        {
            logger.LogInformation("Monitoring {Id}: seed job busy ({Status}) — skip cycle",
                monitoringId, job.Status);
            return;
        }

        var cycleStart = DateTimeOffset.UtcNow;
        var dateFrom = config.LastCollectedAt ?? config.CreatedAt;
        var lastNum = await db.MonitoringCycles
            .Where(c => c.MonitoringId == monitoringId)
            .Select(c => (int?)c.CycleNumber)
            .MaxAsync(Ct) ?? -1;

        var cycle = new MonitoringCycle
        {
            MonitoringId = monitoringId,
            CycleNumber = lastNum + 1,
            StartedAt = cycleStart,
            PeriodFrom = dateFrom,
            PeriodTo = cycleStart,
            Status = MonitoringCycleStatus.Running,
        };
        db.MonitoringCycles.Add(cycle);
        await db.SaveChangesAsync(Ct);

        var specsBySource = await BuildRestartSpecsAsync(config);
        if (specsBySource.Count == 0)
        {
            await FailCycleAsync(config, cycle, "Нет активных карточек филиалов для мониторинга.");
            return;
        }

        var anyStarted = false;
        foreach (var (source, specs) in specsBySource)
        {
            try
            {
                await gateway.RestartSourceAsync(
                    config.SeedJobId, source,
                    new RestartSourceQaRequest(specs, dateFrom, cycleStart), Ct);
                anyStarted = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Monitoring {Id}: restart-source {Source} failed", monitoringId, source);
            }
        }

        if (!anyStarted)
        {
            await FailCycleAsync(config, cycle, "Не удалось запустить сбор ни по одному источнику.");
            await SafeNotifyAdminAsync(config,
                $"Цикл #{cycle.CycleNumber}: не удалось запустить сбор ни по одному источнику.");
            return;
        }

        logger.LogInformation(
            "Monitoring {Id}: cycle #{Num} started (dateFrom={From:o}, sources={Sources})",
            monitoringId, cycle.CycleNumber, dateFrom, string.Join(",", specsBySource.Keys));
    }

    public async Task ReconcilePendingAsync()
    {
        var openCycles = await db.MonitoringCycles
            .Where(c => c.FinishedAt == null && c.Status == MonitoringCycleStatus.Running)
            .OrderBy(c => c.StartedAt)
            .ToListAsync(Ct);
        if (openCycles.Count == 0)
            return;

        foreach (var cycle in openCycles)
        {
            var config = await db.MonitoringConfigs.FirstOrDefaultAsync(m => m.Id == cycle.MonitoringId, Ct);
            if (config is null)
            {
                cycle.Status = MonitoringCycleStatus.Failed;
                cycle.FinishedAt = DateTimeOffset.UtcNow;
                cycle.Error = "Конфиг мониторинга удалён.";
                await db.SaveChangesAsync(Ct);
                continue;
            }

            AnalysisJobDto? job;
            try
            {
                job = await gateway.GetAnalysisAsync(config.SeedJobId, Ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconcile: PG GetAnalysis failed for monitoring {Id} — retry later",
                    config.Id);
                continue;
            }

            if (job is null)
            {
                cycle.Status = MonitoringCycleStatus.Failed;
                cycle.FinishedAt = DateTimeOffset.UtcNow;
                cycle.Error = "Seed job не найден в Processing Gateway.";
                config.LastRunStatus = MonitoringCycleStatus.Failed;
                config.Status = MonitoringStatus.Error;
                config.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(Ct);
                continue;
            }

            if (!IsTerminal(job.Status))
                continue; // ещё идёт — заберём в следующий тик

            await FinalizeCycleAsync(config, cycle, job);
        }
    }

    private async Task FinalizeCycleAsync(MonitoringConfig config, MonitoringCycle cycle, AnalysisJobDto job)
    {
        var now = DateTimeOffset.UtcNow;
        var cycleStatus = MapStatus(job.Status);

        // Авторитетный «new per cycle» — сумма newReviewCount по источникам из PG (PG #2).
        // Если PG ещё не отдаёт поле (нет ни одного non-null) — null, тогда фолбэк на
        // read-only оценку по collected_at из stats.
        int? pgNewCount = job.CollectionProgress.Values.Any(e => e.NewReviewCount.HasValue)
            ? job.CollectionProgress.Values.Sum(e => e.NewReviewCount ?? 0)
            : null;

        if (stats is not null)
        {
            try
            {
                var s = await stats.ComputeCycleStatsAsync(config.SeedJobId, cycle.StartedAt, Ct);
                cycle.NewReviewCount = pgNewCount ?? s.NewReviewCount;
                cycle.TotalReviewsAtCycle = s.TotalReviews;
                cycle.NegativeRatioPp = s.NegativeRatioPp;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconcile: cycle stats failed for monitoring {Id}", config.Id);
                if (pgNewCount.HasValue) cycle.NewReviewCount = pgNewCount.Value;
            }
        }
        else
        {
            // ProcessingReadDb не настроен (нет тональности/total), но new-count из PG доступен.
            if (pgNewCount.HasValue) cycle.NewReviewCount = pgNewCount.Value;
            logger.LogWarning(
                "Reconcile: ProcessingReadDb not configured — only PG new-count used (monitoring {Id})",
                config.Id);
        }

        if (recommendations is not null)
        {
            try
            {
                var list = await recommendations.ListByJobAsync(config.SeedJobId, Ct);
                cycle.RecommendationsSnapshot = list
                    .Select(r => new RecommendationSnapshotItem
                    {
                        Priority = r.Priority,
                        Topic = r.Topic,
                        Title = r.Title,
                        Body = r.Body,
                        ExpectedImpact = r.ExpectedImpact,
                        Evidence = r.Evidence.ToList(),
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconcile: recommendations snapshot failed for monitoring {Id}", config.Id);
            }
        }
        cycle.SummarySnapshot = job.Summary;

        // Правило «резкий рост негатива»: сравниваем срез текущего цикла с предыдущим РЕАЛЬНЫМ
        // циклом (CycleNumber > 0, не baseline). Первый реальный цикл сравнивать не с чем → без алерта.
        var prev = await db.MonitoringCycles
            .Where(c => c.MonitoringId == config.Id
                        && c.Id != cycle.Id
                        && c.CycleNumber > 0
                        && c.FinishedAt != null
                        && (c.Status == MonitoringCycleStatus.Success || c.Status == MonitoringCycleStatus.Partial))
            .OrderByDescending(c => c.CycleNumber)
            .FirstOrDefaultAsync(Ct);

        var spike = options.Value.NegativeSpike;
        if (prev is not null
            && cycle.NewReviewCount >= spike.MinNewReviews
            && (cycle.NegativeRatioPp - prev.NegativeRatioPp) >= spike.MinIncreasePp)
        {
            cycle.NegativeSpikeTriggered = true;
        }

        // Успешный цикл без новых отзывов — это не «успех» и не «ошибка», а «нет новых».
        // (Partial/Failed оставляем как есть — там был реальный сбой источника.)
        var effectiveStatus =
            cycleStatus == MonitoringCycleStatus.Success && cycle.NewReviewCount == 0
                ? MonitoringCycleStatus.NoNewReviews
                : cycleStatus;

        cycle.Status = effectiveStatus;
        cycle.FinishedAt = now;

        // Watermark двигаем при любом не-failed исходе (успех/partial/нет-новых) — окно проверено
        // вплоть до cycleStart; на failed оставляем старый, чтобы следующий цикл перебрал то же окно.
        if (effectiveStatus is MonitoringCycleStatus.Success
            or MonitoringCycleStatus.Partial
            or MonitoringCycleStatus.NoNewReviews)
            config.LastCollectedAt = cycle.StartedAt;
        config.LastRunStatus = effectiveStatus;
        config.Status = effectiveStatus == MonitoringCycleStatus.Failed
            ? MonitoringStatus.Error
            : (config.Status == MonitoringStatus.Error ? MonitoringStatus.Active : config.Status);
        config.UpdatedAt = now;

        await db.SaveChangesAsync(Ct);

        // Недоступные источники цикла — ТОЛЬКО среди источников этого мониторинга (растущий seed-job
        // может содержать «чужие» источники с устаревшим не-completed статусом, их не показываем).
        var unavailableSources = job.CollectionProgress
            .Where(kv => config.Sources.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
                         && !string.Equals(kv.Value.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .Select(kv => BranchSources.Label(kv.Key))
            .Distinct()
            .ToList();

        // Точки уведомлений (Telegram). Сбой доставки внутри модуля не пробрасывается.
        try
        {
            await notifications.SendMonitoringCycleResultAsync(
                config.UserId, config.Id, effectiveStatus.Wire(),
                cycle.NewReviewCount, cycle.PeriodFrom, cycle.PeriodTo, unavailableSources, Ct);

            if (cycle.NegativeSpikeTriggered && prev is not null)
                await notifications.SendNegativeSentimentAlertAsync(
                    config.UserId, config.Id, prev.NegativeRatioPp, cycle.NegativeRatioPp,
                    cycle.NewReviewCount, Ct);

            // Алерт админу — только на реальные проблемы (partial/failed), не на «нет новых».
            if (effectiveStatus is MonitoringCycleStatus.Partial or MonitoringCycleStatus.Failed)
            {
                var companyName = await db.Companies.AsNoTracking()
                    .Where(c => c.Id == config.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(Ct);
                await notifications.SendAdminAlertAsync(
                    BuildCycleAdminAlert(config, cycle, effectiveStatus, unavailableSources, job, companyName), Ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reconcile: notification send failed (monitoring {Id})", config.Id);
        }

        logger.LogInformation(
            "Monitoring {Id}: cycle #{Num} finalized {Status}, +{New} new, neg={Neg:0.0}pp, spike={Spike}",
            config.Id, cycle.CycleNumber, effectiveStatus, cycle.NewReviewCount, cycle.NegativeRatioPp,
            cycle.NegativeSpikeTriggered);
    }

    private async Task FailCycleAsync(MonitoringConfig config, MonitoringCycle cycle, string error)
    {
        var now = DateTimeOffset.UtcNow;
        cycle.Status = MonitoringCycleStatus.Failed;
        cycle.FinishedAt = now;
        cycle.Error = error;
        config.LastRunStatus = MonitoringCycleStatus.Failed;
        config.Status = MonitoringStatus.Error;
        config.UpdatedAt = now;
        await db.SaveChangesAsync(Ct);
        logger.LogWarning("Monitoring {Id}: cycle #{Num} failed — {Error}",
            config.Id, cycle.CycleNumber, error);
    }

    // Карточки провайдеров для мониторимых филиалов/источников → RestartSource specs per источник.
    private async Task<Dictionary<string, List<RestartSourceBranchSpec>>> BuildRestartSpecsAsync(
        MonitoringConfig config)
    {
        var branchIds = config.BranchIds;
        var sources = config.Sources;

        var cards = await db.CompanyBranches.AsNoTracking()
            .Where(b => b.LogicalBranchId != null
                        && branchIds.Contains(b.LogicalBranchId.Value)
                        && sources.Contains(b.Source)
                        && b.IsSelected
                        && b.ExternalId != ""
                        && b.ExternalUrl != null)
            .Select(b => new
            {
                b.Source,
                LogicalBranchId = b.LogicalBranchId!.Value,
                b.ExternalId,
                ExternalUrl = b.ExternalUrl!,
            })
            .ToListAsync(Ct);

        return cards
            .GroupBy(c => c.Source)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new RestartSourceBranchSpec(c.LogicalBranchId, c.ExternalId, c.ExternalUrl))
                      .ToList());
    }

    private async Task SafeNotifyAdminAsync(MonitoringConfig config, string reason)
    {
        try
        {
            var companyName = await db.Companies.AsNoTracking()
                .Where(c => c.Id == config.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(Ct);
            await notifications.SendAdminAlertAsync(new AdminAlert(
                Stage: "Мониторинг",
                Reason: reason,
                Severity: "critical",
                EventId: Guid.NewGuid().ToString("N"),
                UserId: config.UserId,
                CompanyId: config.CompanyId,
                CompanyName: companyName,
                JobId: config.SeedJobId), Ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin alert send failed (monitoring {Id})", config.Id);
        }
    }

    // Admin-алерт по итогу проблемного цикла (partial/failed) с минимальным набором полей (ТЗ §3).
    private static AdminAlert BuildCycleAdminAlert(
        MonitoringConfig config, MonitoringCycle cycle, MonitoringCycleStatus status,
        IReadOnlyList<string> unavailable, AnalysisJobDto job, string? companyName)
    {
        var reason = cycle.Error
            ?? (unavailable.Count > 0
                ? $"Цикл #{cycle.CycleNumber}: недоступны источники — {string.Join(", ", unavailable)}"
                : !string.IsNullOrWhiteSpace(job.Error)
                    ? $"Цикл #{cycle.CycleNumber} (status {status.Wire()}): {job.Error}"
                    : $"Цикл #{cycle.CycleNumber} завершился со статусом {status.Wire()}");
        return new AdminAlert(
            Stage: "Мониторинг",
            Reason: reason,
            Severity: status == MonitoringCycleStatus.Failed ? "critical" : "warning",
            EventId: Guid.NewGuid().ToString("N"),
            UserId: config.UserId,
            CompanyId: config.CompanyId,
            CompanyName: companyName,
            JobId: config.SeedJobId);
    }

    // Терминальные статусы PG analysis_jobs.
    private static bool IsTerminal(string status)
        => status is "completed" or "partial" or "failed";

    private static MonitoringCycleStatus MapStatus(string jobStatus) => jobStatus switch
    {
        "completed" => MonitoringCycleStatus.Success,
        "partial" => MonitoringCycleStatus.Partial,
        _ => MonitoringCycleStatus.Failed,
    };
}
