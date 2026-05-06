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

/// QA-эндпоинты для отладки LLM-стороны pipeline. Все 4 ручки нужны до прихода
/// реального LLM (стуб + manual recovery), и `/timeout` останется полезным даже
/// когда реализуем reconciler (Этап 7 отложен).
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

    /// Принять `LlmOutput` в теле, записать в S3 от имени PG и опубликовать
    /// `LlmResultMessage{ status: finished, result_url: ... }`. Главный
    /// инструмент отладки ингеста: позволяет тестировать произвольные ответы
    /// LLM без зависимости от стуба.
    [HttpPost("inject/{jobId:guid}")]
    public async Task<IActionResult> Inject(
        Guid jobId,
        [FromBody] LlmOutput output,
        CancellationToken ct)
    {
        // Корректируем analysis_job_id чтобы клиенту не нужно было дублировать
        // jobId в URL и в теле. Это удобство для curl-ручного использования.
        if (output.AnalysisJobId != jobId)
            output = output with { AnalysisJobId = jobId };

        await _blob.WriteOutputAsync(jobId, output, ct);

        var bucket = _configuration["S3:BucketName"]!;
        var resultUrl = $"s3://{bucket}/{jobId}/output.json";

        await _publisher.Publish(new LlmResultMessage(
            AnalysisJobId: jobId,
            Status: "finished",
            ResultUrl: resultUrl,
            SchemaVersion: output.SchemaVersion,
            Error: null), ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA inject: wrote output.json + published finished for {AnalysisJobId} ({ProcessedCount} processed)",
            jobId, output.ProcessedReview.Count);

        return Accepted(new { result_url = resultUrl, processed_count = output.ProcessedReview.Count });
    }

    /// Опубликовать `LlmResultMessage{ status: failed }` для job-а (тест failure path).
    [HttpPost("fail/{jobId:guid}")]
    public async Task<IActionResult> Fail(
        Guid jobId,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var msg = new LlmResultMessage(
            AnalysisJobId: jobId,
            Status: "failed",
            ResultUrl: null,
            SchemaVersion: LlmDispatcher.CurrentSchemaVersion,
            Error: error ?? "manual fail via QA");

        await _publisher.Publish(msg, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("QA fail: published failed for {AnalysisJobId}", jobId);
        return Accepted(new { published = "LlmResultMessage(failed)", error = msg.Error });
    }

    /// Перечитать reviews job-а, перезаписать input.json и заново опубликовать
    /// LLM-request. Идемпотентно: ингест итогового output отбросит дубль через
    /// UNIQUE (review_id, analysis_job_id).
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

    /// Сдвинуть `analysis_jobs.sent_at` в прошлое. Заготовка для тестов будущего
    /// LlmStatusReconciler (Этап 7) — он берёт jobs со `sent_at < NOW() - timeout`.
    /// Сейчас просто помогает форсировать reconcile-сценарий вручную.
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
}
