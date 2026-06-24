using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;
using LogContext = Serilog.Context.LogContext;

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
        using var _ = LogContext.PushProperty("AnalysisJobId", jobId);
        var job = await _db.AnalysisJobs.SingleAsync(j => j.Id == jobId, ct);
        using var __ = LogContext.PushProperty("CompanyId", job.CompanyId);

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

        // Отзывы без текста (только звёзды) не имеют смысла для LLM-анализа — отбрасываем
        // их из input.json, чтобы не жечь OpenRouter-квоту на пустые промпты. Сами записи
        // остаются в `reviews` и `analysis_job_reviews` — их `stars` всё равно учитываются
        // Web API Analytics-модулем при расчёте рейтинговых агрегатов.
        var reviewsForLlm = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.RawText))
            .ToList();
        var skippedEmpty = reviews.Count - reviewsForLlm.Count;

        if (reviewsForLlm.Count == 0)
        {
            _logger.LogWarning(
                "LlmDispatcher: job {AnalysisJobId} has {Collected} linked reviews but all are empty-text; failing job",
                jobId, reviews.Count);
            job.Status = AnalysisJobStatus.Failed;
            job.Error = "All collected reviews have empty text — nothing to analyze";
            job.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var input = new LlmInput(
            SchemaVersion: CurrentSchemaVersion,
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            Reviews: reviewsForLlm.Select(r => new LlmInputReview(
                ReviewId: r.Id,
                Text: r.RawText,
                Source: r.Source,
                Date: r.ReviewDate,
                Stars: r.Stars,
                BranchId: r.BranchId,
                TextLanguage: r.TextLanguage)).ToList(),
            // Бизнес-контекст компании (снимок из StartAnalysisCommand). Опционален — может быть null.
            BusinessCategory: job.BusinessCategory,
            BusinessSubcategory: job.BusinessSubcategory,
            AdditionalContext: job.AdditionalContext);

        await _blob.WriteInputAsync(jobId, input, ct);

        var bucket = _configuration["S3:BucketName"]!;
        var payloadUrl = $"s3://{bucket}/{jobId}/input.json";
        var requestQueue = _configuration["Llm:RequestQueue"] ?? "llm.requests";
        var callbackQueue = _configuration["Llm:ResultQueue"] ?? "llm.results";

        job.PayloadUrl = payloadUrl;
        job.SentAt = DateTimeOffset.UtcNow;
        job.Status = AnalysisJobStatus.SentToLlm;
        // ReviewCount = всё, что собрано и привязано к job (для UI/Status API).
        // В LLM ушло `reviewsForLlm.Count` — это видно из input.json и LlmRequestMessage.
        job.ReviewCount = reviews.Count;

        // Send ДО SaveChanges — Outbox присоединит сообщение к транзакции и отправит после commit.
        var endpoint = await _sendEndpoints.GetSendEndpoint(new Uri($"queue:{requestQueue}"));
        // Envelope CorrelationId = AnalysisJobId (ADR-008/CLAUDE.md) — сквозной трейс LLM-запроса
        // через outbox/_error. Python-LLM читает body (snake_case), envelope ему безразличен —
        // wire-контракт LlmRequestMessage не меняем.
        await endpoint.Send(new LlmRequestMessage(
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            PayloadUrl: payloadUrl,
            ReviewCount: reviewsForLlm.Count,
            SchemaVersion: CurrentSchemaVersion,
            CallbackQueue: callbackQueue),
            sendCtx => sendCtx.CorrelationId = jobId, ct);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LLM request sent to {Queue} for job {AnalysisJobId}: {SentCount} reviews sent " +
            "({Collected} collected, {SkippedEmpty} skipped as empty-text), payload={PayloadUrl}",
            requestQueue, jobId, reviewsForLlm.Count, reviews.Count, skippedEmpty, payloadUrl);
    }
}
