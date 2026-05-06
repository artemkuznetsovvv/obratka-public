using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Application.Pipeline;

/// Собирает `input.json` для LLM, заливает в S3, публикует `LlmRequestMessage`.
/// Публикация идёт через `IPublishEndpoint` MassTransit — Outbox перед commit
/// обеспечивает атомарность с обновлениями БД.
public sealed class LlmDispatcher
{
    public const string CurrentSchemaVersion = "2.0";

    private readonly ProcessingDbContext _db;
    private readonly IJobBlobStorage _blob;
    private readonly IPublishEndpoint _publisher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LlmDispatcher> _logger;

    public LlmDispatcher(
        ProcessingDbContext db,
        IJobBlobStorage blob,
        IPublishEndpoint publisher,
        IConfiguration configuration,
        ILogger<LlmDispatcher> logger)
    {
        _db = db;
        _blob = blob;
        _publisher = publisher;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DispatchAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.AnalysisJobs.SingleAsync(j => j.Id == jobId, ct);

        // Достаём отзывы, привязанные к job через analysis_job_reviews. Это решает «какие
        // именно отзывы пошли в этот анализ» (вариант B Этапа 5).
        var reviews = await _db.AnalysisJobReviews
            .Where(l => l.AnalysisJobId == jobId)
            .Join(_db.Reviews, l => l.ReviewId, r => r.Id, (_, r) => r)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        if (reviews.Count == 0)
        {
            _logger.LogWarning(
                "LlmDispatcher: job {AnalysisJobId} has 0 linked reviews; failing job",
                jobId);
            job.Status = AnalysisJobStatus.Failed;
            job.Error = "No reviews collected from any source";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var input = new LlmInput(
            SchemaVersion: CurrentSchemaVersion,
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            Reviews: reviews.Select(r => new LlmInputReview(
                ReviewId: r.Id,
                Text: r.RawText,
                Source: r.Source,
                Date: r.ReviewDate,
                Stars: r.Stars,
                BranchId: r.BranchId,
                TextLanguage: r.TextLanguage)).ToList());

        await _blob.WriteInputAsync(jobId, input, ct);

        var bucket = _configuration["S3:BucketName"]!;
        var payloadUrl = $"s3://{bucket}/{jobId}/input.json";
        var callbackQueue = _configuration["Llm:ResultQueue"] ?? "llm.results";

        job.PayloadUrl = payloadUrl;
        job.SentAt = DateTimeOffset.UtcNow;
        job.Status = AnalysisJobStatus.SentToLlm;
        job.ReviewCount = reviews.Count;

        // Publish ДО SaveChanges — Outbox присоединит сообщение к транзакции и отправит после commit.
        await _publisher.Publish(new LlmRequestMessage(
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            PayloadUrl: payloadUrl,
            ReviewCount: reviews.Count,
            SchemaVersion: CurrentSchemaVersion,
            CallbackQueue: callbackQueue), ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM request published for job {AnalysisJobId}: {ReviewCount} reviews, payload={PayloadUrl}",
            jobId, reviews.Count, payloadUrl);
    }
}
