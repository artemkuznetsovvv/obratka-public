using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Consumers;

/// Финализатор: Web API Analytics-модуль закончил подсчёт агрегатов и шлёт событие.
/// Переводим job из `computing_aggregates` в `completed` или `partial` (если в
/// `collection_progress` был хоть один failed-источник).
///
/// На MVP без Web API сюда сообщения не приходят — для dev-стенда поднимается
/// AutoFinalizeService (по флагу), который выполняет ту же FSM-логику по таймеру.
public sealed class AggregatesReadyEventConsumer : IConsumer<AggregatesReadyEvent>
{
    private readonly ProcessingDbContext _db;
    private readonly ILogger<AggregatesReadyEventConsumer> _logger;

    public AggregatesReadyEventConsumer(
        ProcessingDbContext db,
        ILogger<AggregatesReadyEventConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<AggregatesReadyEvent> context)
    {
        var msg = context.Message;
        using var _ = LogContext.PushProperty("AnalysisJobId", msg.AnalysisJobId);

        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(
            j => j.Id == msg.AnalysisJobId, context.CancellationToken);

        if (job is null)
        {
            _logger.LogWarning("AggregatesReady for unknown job {AnalysisJobId} — ignoring", msg.AnalysisJobId);
            return;
        }

        // Идемпотентность.
        if (job.Status is AnalysisJobStatus.Completed or AnalysisJobStatus.Partial or AnalysisJobStatus.Failed)
            return;

        if (job.Status != AnalysisJobStatus.ComputingAggregates)
        {
            _logger.LogWarning(
                "Job {AnalysisJobId} not in 'computing_aggregates' (status={Status}); finalize anyway",
                msg.AnalysisJobId, job.Status);
        }

        var partial = job.CollectionProgress.Values.Any(e => e.Status == "failed");
        job.Status = partial ? AnalysisJobStatus.Partial : AnalysisJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Job {AnalysisJobId} → {Status}", msg.AnalysisJobId, job.Status);
    }
}
