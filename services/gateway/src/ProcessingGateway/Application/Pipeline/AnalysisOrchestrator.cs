using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Pipeline;

/// FSM-переходы `analysis_jobs.status` после ингеста сборщика. Вызывается из
/// `ParserPoller` когда все задачи Parser перешли в completed/failed.
///
/// Все источники failed → status='failed' + AnalysisCompletedEvent(failed).
/// Иначе → status='sent_to_llm' через LlmDispatcher (он сам публикует LlmRequest).
public sealed class AnalysisOrchestrator
{
    private readonly ProcessingDbContext _db;
    private readonly LlmDispatcher _llmDispatcher;
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<AnalysisOrchestrator> _logger;

    public AnalysisOrchestrator(
        ProcessingDbContext db,
        LlmDispatcher llmDispatcher,
        IPublishEndpoint publisher,
        ILogger<AnalysisOrchestrator> logger)
    {
        _db = db;
        _llmDispatcher = llmDispatcher;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task AdvanceAfterCollectionAsync(Guid jobId, CancellationToken ct = default)
    {
        using var _ = LogContext.PushProperty("AnalysisJobId", jobId);
        var job = await _db.AnalysisJobs.SingleAsync(j => j.Id == jobId, ct);
        using var __ = LogContext.PushProperty("CompanyId", job.CompanyId);

        if (job.Status != AnalysisJobStatus.Collecting)
        {
            _logger.LogDebug(
                "AdvanceAfterCollection: job {AnalysisJobId} not in 'collecting' (status={Status}), no-op",
                jobId, job.Status);
            return;
        }

        var progress = job.CollectionProgress;
        var allFailed = progress.Count > 0 && progress.Values.All(e => e.Status == "failed");

        if (allFailed)
        {
            job.Status = AnalysisJobStatus.Failed;
            job.Error = "All sources failed to collect reviews";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _publisher.Publish(new AnalysisCompletedEvent(
                jobId, job.CompanyId,
                AnalysisCompletionStatus.Failed,
                ReviewCount: 0), pubCtx => pubCtx.CorrelationId = jobId, ct);

            _logger.LogWarning("Job {AnalysisJobId} → failed (all sources failed)", jobId);
            return;
        }

        // Оптимизация цикла мониторинга: если все источники отработали без ошибок, но новых
        // отзывов не принесли (sum NewReviewCount == 0), а у job-а уже есть собранные ранее
        // отзывы — нет смысла гонять LLM заново (результат не изменится). Оставляем прежние
        // результаты, job остаётся completed. Это избавляет от лишних LLM-прогонов на пустых
        // циклах (модель «растущий seed-job»).
        var anyFailed = progress.Values.Any(e => e.Status == "failed");
        var newThisCycle = progress.Values.Sum(e => e.NewReviewCount ?? 0);
        if (!anyFailed && newThisCycle == 0)
        {
            var hasExisting = await _db.AnalysisJobReviews.AnyAsync(l => l.AnalysisJobId == jobId, ct);
            if (hasExisting)
            {
                job.Status = AnalysisJobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.Error = null;
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Job {AnalysisJobId}: re-collection found 0 new reviews — keeping previous results, LLM skipped",
                    jobId);
                return;
            }
        }

        // Есть новые отзывы (или это первый сбор) — двигаем в LLM.
        await _llmDispatcher.DispatchAsync(jobId, ct);
    }
}
