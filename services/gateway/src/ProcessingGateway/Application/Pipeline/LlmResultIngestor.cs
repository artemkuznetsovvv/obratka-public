using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Application.Pipeline;

/// Принимает `LlmResultMessage`, читает `output.json` из S3, сохраняет per-review
/// результаты + recommendation, переводит job в `computing_aggregates` и публикует
/// `AnalysisCompletedEvent` для Web API Analytics.
///
/// Идемпотентен: повторное применение одного и того же output.json безопасно (UNIQUE
/// review_id+analysis_job_id + проверка статуса перед переходом).
public sealed class LlmResultIngestor
{
    private readonly ProcessingDbContext _db;
    private readonly IJobBlobStorage _blob;
    private readonly ReviewLlmResultBulkInserter _inserter;
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<LlmResultIngestor> _logger;

    public LlmResultIngestor(
        ProcessingDbContext db,
        IJobBlobStorage blob,
        ReviewLlmResultBulkInserter inserter,
        IPublishEndpoint publisher,
        ILogger<LlmResultIngestor> logger)
    {
        _db = db;
        _blob = blob;
        _inserter = inserter;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task IngestFinishedAsync(Guid jobId, string resultUrl, CancellationToken ct = default)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            _logger.LogWarning(
                "LlmResult finished received for unknown job {AnalysisJobId} — ignoring",
                jobId);
            return;
        }

        // Идемпотентность: если уже завершили обработку, повторное применение не нужно.
        if (job.Status is AnalysisJobStatus.ComputingAggregates
                       or AnalysisJobStatus.Completed
                       or AnalysisJobStatus.Partial)
        {
            _logger.LogInformation(
                "Job {AnalysisJobId} already in {Status}, skipping duplicate LLM result",
                jobId, job.Status);
            return;
        }

        var output = await _blob.ReadOutputAsync(jobId, ct);

        if (output.AnalysisJobId != jobId)
        {
            _logger.LogError(
                "output.json declared analysis_job_id={DeclaredJobId} but expected {ExpectedJobId}",
                output.AnalysisJobId, jobId);
            await FailAsync(job, $"LLM output mismatch: expected {jobId}, got {output.AnalysisJobId}", ct);
            return;
        }

        if (!string.Equals(output.SchemaVersion, LlmDispatcher.CurrentSchemaVersion,
            StringComparison.OrdinalIgnoreCase))
        {
            // Решение по эволюции схемы (см. диалог: tolerant deserialization + warning).
            // На MAJOR-конфликте лучше отбросить, на MINOR — продолжить. Сейчас единственный.
            _logger.LogWarning(
                "LLM output schema_version={Got} differs from expected {Expected}; continuing tolerant",
                output.SchemaVersion, LlmDispatcher.CurrentSchemaVersion);
        }

        var inserted = await _inserter.InsertAsync(jobId, output.ProcessedReview, ct);
        _logger.LogInformation(
            "Saved {Inserted} LLM results for job {AnalysisJobId} ({TotalProvided} provided by LLM)",
            inserted, jobId, output.ProcessedReview.Count);

        job.Recommendation = output.Recommendation;
        job.ResultUrl = resultUrl;
        job.Status = AnalysisJobStatus.ComputingAggregates;

        // Решаем completed_pending_aggregates vs partial по наличию failed-источников.
        var partial = job.CollectionProgress.Values.Any(e => e.Status == "failed");
        var completionStatus = partial
            ? AnalysisCompletionStatus.Partial
            : AnalysisCompletionStatus.CompletedPendingAggregates;

        await _publisher.Publish(new AnalysisCompletedEvent(
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            Status: completionStatus,
            ReviewCount: job.ReviewCount), ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Job {AnalysisJobId} → computing_aggregates ({CompletionStatus})",
            jobId, completionStatus);
    }

    public async Task IngestFailedAsync(Guid jobId, string? error, CancellationToken ct = default)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        if (job.Status == AnalysisJobStatus.Failed) return;

        await FailAsync(job, error ?? "LLM reported failed without details", ct);
    }

    private async Task FailAsync(AnalysisJob job, string error, CancellationToken ct)
    {
        job.Status = AnalysisJobStatus.Failed;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;

        await _publisher.Publish(new AnalysisCompletedEvent(
            AnalysisJobId: job.Id,
            CompanyId: job.CompanyId,
            Status: AnalysisCompletionStatus.Failed,
            ReviewCount: job.ReviewCount), ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("Job {AnalysisJobId} → failed: {Error}", job.Id, error);
    }
}
