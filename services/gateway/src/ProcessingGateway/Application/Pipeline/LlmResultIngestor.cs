using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Application.Pipeline;

/// Принимает `LlmResultMessage`, читает `output_reviews.json` + `output_summary.json` из S3,
/// сохраняет per-review результаты + summary + recommendations, переводит job в
/// `computing_aggregates` и публикует `AnalysisCompletedEvent` для Web API Analytics.
///
/// Идемпотентен: повторное применение одного и того же result безопасно (UNIQUE
/// review_id+analysis_job_id для review_llm_results, DELETE+INSERT для analysis_recommendations,
/// проверка статуса перед переходом).
public sealed class LlmResultIngestor
{
    private readonly ProcessingDbContext _db;
    private readonly IJobBlobStorage _blob;
    private readonly ReviewLlmResultBulkInserter _reviewsInserter;
    private readonly AnalysisRecommendationWriter _recommendationsWriter;
    private readonly IPublishEndpoint _publisher;
    private readonly ILogger<LlmResultIngestor> _logger;

    public LlmResultIngestor(
        ProcessingDbContext db,
        IJobBlobStorage blob,
        ReviewLlmResultBulkInserter reviewsInserter,
        AnalysisRecommendationWriter recommendationsWriter,
        IPublishEndpoint publisher,
        ILogger<LlmResultIngestor> logger)
    {
        _db = db;
        _blob = blob;
        _reviewsInserter = reviewsInserter;
        _recommendationsWriter = recommendationsWriter;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task IngestFinishedAsync(
        Guid jobId,
        string resultReviewsUrl,
        string resultSummaryUrl,
        CancellationToken ct = default)
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

        // Скачиваем оба файла. Ошибка в любом — job → failed.
        var reviewsOutput  = await _blob.ReadReviewsOutputAsync(resultReviewsUrl, ct);
        var summaryOutput  = await _blob.ReadSummaryOutputAsync(resultSummaryUrl, ct);

        // Валидация: оба analysis_job_id должны совпадать с request.
        if (reviewsOutput.AnalysisJobId != jobId)
        {
            await FailAsync(job,
                $"output_reviews.json analysis_job_id mismatch: expected {jobId}, got {reviewsOutput.AnalysisJobId}",
                ct);
            return;
        }
        if (summaryOutput.AnalysisJobId != jobId)
        {
            await FailAsync(job,
                $"output_summary.json analysis_job_id mismatch: expected {jobId}, got {summaryOutput.AnalysisJobId}",
                ct);
            return;
        }

        // Schema_version warning-only — tolerant deserializer (см. диалог по эволюции схемы).
        if (!string.Equals(reviewsOutput.SchemaVersion, LlmDispatcher.CurrentSchemaVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "output_reviews.json schema_version={Got} differs from expected {Expected}; continuing tolerant",
                reviewsOutput.SchemaVersion, LlmDispatcher.CurrentSchemaVersion);
        }
        if (!string.Equals(summaryOutput.SchemaVersion, LlmDispatcher.CurrentSchemaVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "output_summary.json schema_version={Got} differs from expected {Expected}; continuing tolerant",
                summaryOutput.SchemaVersion, LlmDispatcher.CurrentSchemaVersion);
        }

        // Инвариант от LLM-команды: recommendations_count == len(full_recommendations).
        if (summaryOutput.RecommendationsCount != summaryOutput.FullRecommendations.Count)
        {
            _logger.LogWarning(
                "output_summary.json recommendations_count={Declared} != len(full_recommendations)={Actual}; using actual",
                summaryOutput.RecommendationsCount, summaryOutput.FullRecommendations.Count);
        }

        // Bulk insert per-review результатов (ON CONFLICT DO NOTHING).
        var insertedReviews = await _reviewsInserter.InsertAsync(jobId, reviewsOutput.Reviews, ct);
        _logger.LogInformation(
            "Saved {Inserted} per-review LLM results for job {AnalysisJobId} ({TotalProvided} provided by LLM)",
            insertedReviews, jobId, reviewsOutput.Reviews.Count);

        // DELETE + bulk INSERT recommendations (replay-идемпотентность).
        var insertedRecs = await _recommendationsWriter.ReplaceAllAsync(
            jobId, summaryOutput.FullRecommendations, ct);
        _logger.LogInformation(
            "Replaced recommendations for job {AnalysisJobId}: {Count} rows",
            jobId, insertedRecs);

        // Обновляем job-метаданные.
        job.Summary = summaryOutput.Summary;
        job.RecommendationsCount = summaryOutput.FullRecommendations.Count;
        job.ResultReviewsUrl = resultReviewsUrl;
        job.ResultSummaryUrl = resultSummaryUrl;
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
