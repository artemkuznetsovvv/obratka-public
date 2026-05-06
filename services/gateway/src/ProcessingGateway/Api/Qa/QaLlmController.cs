using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Application.Pipeline;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;

namespace ProcessingGateway.Api.Qa;

/// QA-эндпоинты для отладки LLM-стороны pipeline (schema 2.0). 4 ручки нужны до прихода
/// реального LLM (стуб + manual recovery), и `/timeout` останется полезным даже когда
/// реализуем reconciler (Этап 7 отложен).
[ApiController]
[Route("api/qa/llm")]
[RequireQaApiKey]
public sealed class QaLlmController : ControllerBase
{
    private readonly ProcessingDbContext _db;
    private readonly IJobBlobStorage _blob;
    private readonly IPublishEndpoint _publisher;
    private readonly LlmDispatcher _dispatcher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QaLlmController> _logger;

    public QaLlmController(
        ProcessingDbContext db,
        IJobBlobStorage blob,
        IPublishEndpoint publisher,
        LlmDispatcher dispatcher,
        IConfiguration configuration,
        ILogger<QaLlmController> logger)
    {
        _db = db;
        _blob = blob;
        _publisher = publisher;
        _dispatcher = dispatcher;
        _configuration = configuration;
        _logger = logger;
    }

    /// Schema 2.0 inject: принимает **оба** output-файла в одном теле, записывает в S3,
    /// публикует `LlmResultMessage(finished)` с обоими URL-ами. Главный инструмент отладки
    /// ингеста без зависимости от стуба или реального LLM.
    [HttpPost("inject/{jobId:guid}")]
    public async Task<IActionResult> Inject(
        Guid jobId,
        [FromBody] InjectRequest body,
        CancellationToken ct)
    {
        // Корректируем analysis_job_id чтобы клиенту не нужно было дублировать его в трёх местах.
        var reviewsOutput = body.Reviews with { AnalysisJobId = jobId };
        var summaryOutput = body.Summary with { AnalysisJobId = jobId };

        await _blob.WriteReviewsOutputAsync(jobId, reviewsOutput, ct);
        await _blob.WriteSummaryOutputAsync(jobId, summaryOutput, ct);

        var bucket = _configuration["S3:BucketName"]!;
        var reviewsUrl = $"s3://{bucket}/{jobId}/output_reviews.json";
        var summaryUrl = $"s3://{bucket}/{jobId}/output_summary.json";

        await _publisher.Publish(new LlmResultMessage(
            AnalysisJobId: jobId,
            Status: "finished",
            ResultReviewsUrl: reviewsUrl,
            ResultSummaryUrl: summaryUrl,
            SchemaVersion: reviewsOutput.SchemaVersion,
            Error: null), ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA inject: wrote outputs + published finished for {AnalysisJobId} " +
            "({Reviews} reviews, {Recs} recommendations)",
            jobId, reviewsOutput.Reviews.Count, summaryOutput.FullRecommendations.Count);

        return Accepted(new
        {
            result_reviews_url = reviewsUrl,
            result_summary_url = summaryUrl,
            reviews_count = reviewsOutput.Reviews.Count,
            recommendations_count = summaryOutput.FullRecommendations.Count
        });
    }

    /// Опубликовать `LlmResultMessage(failed)` для job-а (тест failure path).
    [HttpPost("fail/{jobId:guid}")]
    public async Task<IActionResult> Fail(
        Guid jobId,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var msg = new LlmResultMessage(
            AnalysisJobId: jobId,
            Status: "failed",
            ResultReviewsUrl: null,
            ResultSummaryUrl: null,
            SchemaVersion: LlmDispatcher.CurrentSchemaVersion,
            Error: error ?? "manual fail via QA");

        await _publisher.Publish(msg, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("QA fail: published failed for {AnalysisJobId}", jobId);
        return Accepted(new { published = "LlmResultMessage(failed)", error = msg.Error });
    }

    /// Перечитать reviews job-а, перезаписать input.json и заново опубликовать LLM-request.
    /// Идемпотентно: ингест итоговых outputs отбросит дубль (UNIQUE review_id+analysis_job_id +
    /// DELETE+INSERT для analysis_recommendations).
    [HttpPost("replay/{jobId:guid}")]
    public async Task<IActionResult> Replay(Guid jobId, CancellationToken ct)
    {
        var exists = await _db.AnalysisJobs.AsNoTracking()
            .AnyAsync(j => j.Id == jobId, ct);
        if (!exists) return NotFound();

        await _dispatcher.DispatchAsync(jobId, ct);
        _logger.LogInformation("QA replay: re-dispatched LLM for {AnalysisJobId}", jobId);
        return Accepted(new { status = "re-dispatched" });
    }

    /// Сдвинуть `analysis_jobs.sent_at` в прошлое — заготовка под reconciler (Этап 7).
    [HttpPost("timeout/{jobId:guid}")]
    public async Task<IActionResult> Timeout(Guid jobId, CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        var oldSentAt = job.SentAt;
        job.SentAt = DateTimeOffset.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA timeout: shifted sent_at for {AnalysisJobId} from {Old} to {New}",
            jobId, oldSentAt, job.SentAt);
        return Ok(new { previous_sent_at = oldSentAt, new_sent_at = job.SentAt });
    }

    /// Тело для `POST /inject/{jobId}` — два output-файла в одном запросе.
    public record InjectRequest(LlmReviewsOutput Reviews, LlmSummaryOutput Summary);
}
