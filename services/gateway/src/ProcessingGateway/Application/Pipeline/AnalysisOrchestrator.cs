using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;

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
        var job = await _db.AnalysisJobs.SingleAsync(j => j.Id == jobId, ct);

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
                ReviewCount: 0), ct);

            _logger.LogWarning("Job {AnalysisJobId} → failed (all sources failed)", jobId);
            return;
        }

        // Хотя бы один источник дал что-то — двигаем в LLM.
        await _llmDispatcher.DispatchAsync(jobId, ct);
    }
}
