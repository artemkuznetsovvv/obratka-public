using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Application.Pipeline;

/// Собирает `input.json` для LLM, заливает в S3, отправляет `LlmRequestMessage`
/// **напрямую в очередь** `llm.requests` через `ISendEndpointProvider.Send`
/// (а не `IPublishEndpoint.Publish`).
///
/// **Почему Send, а не Publish.** LLM-сервис на Python не использует MassTransit и
/// подписан на конкретную AMQP-очередь `llm.requests`. `Publish` через MassTransit
/// маршрутизирует сообщение в **fanout exchange** с именем класса (`LlmRequestMessage`),
/// который привязан к одноимённой queue, но **не** к `llm.requests` — между ними нет
/// binding. Send явно адресует очередь и обходит exchange-routing.
///
/// EF Outbox поддерживает Send из scoped DI так же, как и Publish: сообщение записывается
/// в outbox-таблицу до commit и фактически отправляется после `SaveChangesAsync`.
public sealed class LlmDispatcher
{
    public const string CurrentSchemaVersion = "2.0";

    private readonly ProcessingDbContext _db;
    private readonly IJobBlobStorage _blob;
    private readonly ISendEndpointProvider _sendEndpoints;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LlmDispatcher> _logger;

    public LlmDispatcher(
        ProcessingDbContext db,
        IJobBlobStorage blob,
        ISendEndpointProvider sendEndpoints,
        IConfiguration configuration,
        ILogger<LlmDispatcher> logger)
    {
        _db = db;
        _blob = blob;
        _sendEndpoints = sendEndpoints;
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
        var requestQueue = _configuration["Llm:RequestQueue"] ?? "llm.requests";
        var callbackQueue = _configuration["Llm:ResultQueue"] ?? "llm.results";

        job.PayloadUrl = payloadUrl;
        job.SentAt = DateTimeOffset.UtcNow;
        job.Status = AnalysisJobStatus.SentToLlm;
        job.ReviewCount = reviews.Count;

        // Send ДО SaveChanges — Outbox присоединит сообщение к транзакции и отправит после commit.
        var endpoint = await _sendEndpoints.GetSendEndpoint(new Uri($"queue:{requestQueue}"));
        await endpoint.Send(new LlmRequestMessage(
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            PayloadUrl: payloadUrl,
            ReviewCount: reviews.Count,
            SchemaVersion: CurrentSchemaVersion,
            CallbackQueue: callbackQueue), ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM request sent to {Queue} for job {AnalysisJobId}: {ReviewCount} reviews, payload={PayloadUrl}",
            requestQueue, jobId, reviews.Count, payloadUrl);
    }
}
