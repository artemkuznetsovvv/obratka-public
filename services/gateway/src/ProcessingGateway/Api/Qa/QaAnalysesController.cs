using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Api;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Api.Qa;

/// Bootstrap-ручка для разработки до прихода Web API. Публикует
/// `StartAnalysisCommand` ровно так же, как это сделает Web API: команда уходит в брокер,
/// `StartAnalysisCommandConsumer` разбирает её. Защищается X-Api-Key (см. RequireQaApiKey)
/// + nginx allowlist на VPS — снаружи закрыто.
[ApiController]
[Route("api/qa/analyses")]
[RequireQaApiKey]
public sealed class QaAnalysesController : ControllerBase
{
    private readonly IPublishEndpoint _publisher;
    private readonly ProcessingDbContext _db;
    private readonly ILogger<QaAnalysesController> _logger;

    public QaAnalysesController(
        IPublishEndpoint publisher,
        ProcessingDbContext db,
        ILogger<QaAnalysesController> logger)
    {
        _publisher = publisher;
        _db = db;
        _logger = logger;
    }

    public sealed record StartAnalysisQaRequest(
        Guid CompanyId,
        DateTimeOffset? DateFrom,
        DateTimeOffset? DateTo,
        IReadOnlyList<BranchSpec> Branches,
        Guid? AnalysisJobId = null);  // override для предсказуемых интеграционных тестов

    public sealed record StartAnalysisQaResponse(Guid AnalysisJobId);

    [HttpPost]
    [ProducesResponseType(typeof(StartAnalysisQaResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartAnalysisQaResponse>> Start(
        [FromBody] StartAnalysisQaRequest request,
        CancellationToken ct)
    {
        if (request.Branches is null || request.Branches.Count == 0)
            return BadRequest(new { error = "branches must contain at least one element" });

        var jobId = request.AnalysisJobId ?? Guid.NewGuid();

        // PG-сторона pivot: здесь рождается AnalysisJobId. CorrelationId запроса уже в LogContext
        // (CorrelationIdMiddleware), поэтому нижняя строка связывает цепочку запроса Web API с
        // новым сквозным трейсом анализа. Envelope CorrelationId=jobId — трассировка команды далее.
        using var _ = LogContext.PushProperty("AnalysisJobId", jobId);
        using var __ = LogContext.PushProperty("CompanyId", request.CompanyId);
        HttpContext.Items["AnalysisJobId"] = jobId; // для строки request-summary (EnrichDiagnosticContext)

        var cmd = new StartAnalysisCommand(
            AnalysisJobId: jobId,
            CompanyId: request.CompanyId,
            DateFrom: request.DateFrom,
            DateTo: request.DateTo,
            Branches: request.Branches);

        await _publisher.Publish(cmd, pubCtx => pubCtx.CorrelationId = jobId, ct);

        // MassTransit EF Outbox: сообщение шипится в брокер только после
        // DbContext.SaveChangesAsync(). Без этого вызова publish осядет в outbox-таблице
        // и никогда не уйдёт. SaveChanges на пустом DbContext безвреден — это просто
        // commit «нет изменений».
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Analysis job {AnalysisJobId} created for company {CompanyId} (branches={BranchCount})",
            jobId, request.CompanyId, request.Branches.Count);

        return AcceptedAtAction(
            actionName: null,
            value: new StartAnalysisQaResponse(jobId));
    }

    // --- Debug snapshot ---

    /// Список анализов с пагинацией и фильтрами. Каждый item — та же форма,
    /// что отдаёт GetSnapshot, чтобы admin-UI мог раскрыть строку и показать
    /// collection_progress без дополнительного запроса.
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery(Name = "companyId")] Guid? companyId,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 500);
        var skip = Math.Max(offset ?? 0, 0);

        AnalysisJobStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            try { statusFilter = AnalysisJobStatusExtensions.FromWire(status); }
            catch (ArgumentException) { return BadRequest(new { error = $"Unknown status: '{status}'" }); }
        }

        var query = _db.AnalysisJobs.AsNoTracking();
        if (companyId is not null) query = query.Where(j => j.CompanyId == companyId.Value);
        if (statusFilter is not null) query = query.Where(j => j.Status == statusFilter.Value);

        var total = await query.CountAsync(ct);
        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var items = jobs.Select(job => new
        {
            id = job.Id,
            company_id = job.CompanyId,
            status = job.Status.ToWire(),
            review_count = job.ReviewCount,
            collection_progress = job.CollectionProgress,
            payload_url = job.PayloadUrl,
            result_reviews_url = job.ResultReviewsUrl,
            result_summary_url = job.ResultSummaryUrl,
            summary = job.Summary,
            recommendations_count = job.RecommendationsCount,
            created_at = job.CreatedAt,
            sent_at = job.SentAt,
            completed_at = job.CompletedAt,
            error = job.Error
        }).ToList();

        return Ok(new { total, limit = take, offset = skip, items });
    }

    /// Полный снимок строки `analysis_jobs` без trim-ов — то, что в БД ровно сейчас.
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshot(Guid jobId, CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        return Ok(new
        {
            id = job.Id,
            company_id = job.CompanyId,
            status = job.Status.ToWire(),
            review_count = job.ReviewCount,
            collection_progress = job.CollectionProgress,
            payload_url = job.PayloadUrl,
            result_reviews_url = job.ResultReviewsUrl,
            result_summary_url = job.ResultSummaryUrl,
            summary = job.Summary,
            recommendations_count = job.RecommendationsCount,
            created_at = job.CreatedAt,
            sent_at = job.SentAt,
            completed_at = job.CompletedAt,
            error = job.Error
        });
    }

    /// Список собранных отзывов для job — что лежит в reviews через analysis_job_reviews.
    [HttpGet("{jobId:guid}/reviews")]
    public async Task<IActionResult> GetReviews(
        Guid jobId,
        [FromQuery] string? source,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 500);
        var skip = Math.Max(offset ?? 0, 0);

        var query = _db.AnalysisJobReviews.AsNoTracking()
            .Where(l => l.AnalysisJobId == jobId)
            .Join(_db.Reviews, l => l.ReviewId, r => r.Id, (_, r) => r);

        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(r => r.Source == source);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(r => r.Id)
            .Skip(skip).Take(take)
            .Select(r => new
            {
                id = r.Id,
                source = r.Source,
                external_id = r.ExternalId,
                stars = r.Stars,
                review_date = r.ReviewDate,
                text_preview = r.RawText.Length > 200 ? r.RawText.Substring(0, 200) : r.RawText
            })
            .ToListAsync(ct);

        return Ok(new { total, limit = take, offset = skip, items });
    }

    /// Агрегаты по (branch_id, source) для собранных в этом job отзывов.
    /// Используется Web API для рендера «Сколько собрано по конкретному филиалу и источнику»
    /// на странице деталей анализа. branch_id здесь — физический (LogicalBranch.Id),
    /// потому что Web API при запуске прокидывает именно его в StartAnalysisBranchSpec.
    [HttpGet("{jobId:guid}/branch-stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBranchStats(Guid jobId, CancellationToken ct)
    {
        var items = await _db.AnalysisJobReviews.AsNoTracking()
            .Where(l => l.AnalysisJobId == jobId)
            .Join(_db.Reviews, l => l.ReviewId, r => r.Id, (_, r) => r)
            .GroupBy(r => new { r.BranchId, r.Source })
            .Select(g => new
            {
                branch_id = g.Key.BranchId,
                source = g.Key.Source,
                review_count = g.Count(),
            })
            .OrderBy(x => x.branch_id).ThenBy(x => x.source)
            .ToListAsync(ct);
        return Ok(new { items });
    }

    /// Принудительно пометить job как failed (для отладки залипших состояний).
    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, [FromQuery] string? reason, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("AnalysisJobId", jobId);
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        if (job.Status is AnalysisJobStatus.Completed or AnalysisJobStatus.Partial or AnalysisJobStatus.Failed)
            return Conflict(new { error = $"Job already in terminal status {job.Status.ToWire()}" });
        using var __ = LogContext.PushProperty("CompanyId", job.CompanyId);

        job.Status = AnalysisJobStatus.Failed;
        job.Error = string.IsNullOrWhiteSpace(reason) ? "manual cancel via QA" : reason;
        job.CompletedAt = DateTimeOffset.UtcNow;

        await _publisher.Publish(new AnalysisCompletedEvent(
            AnalysisJobId: jobId,
            CompanyId: job.CompanyId,
            Status: AnalysisCompletionStatus.Failed,
            ReviewCount: job.ReviewCount), pubCtx => pubCtx.CorrelationId = jobId, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("QA cancel: job {AnalysisJobId} → failed: {Error}", jobId, job.Error);
        return Ok(new { status = job.Status.ToWire(), error = job.Error });
    }

    /// Имитация AggregatesReadyEvent от Web API: не дожидаясь AutoFinalize-таймера,
    /// финализируем job, который завис в `computing_aggregates`.
    [HttpPost("{jobId:guid}/finalize")]
    public async Task<IActionResult> Finalize(Guid jobId, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("AnalysisJobId", jobId);
        var job = await _db.AnalysisJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        using var __ = LogContext.PushProperty("CompanyId", job.CompanyId);

        await _publisher.Publish(new AggregatesReadyEvent(jobId, job.CompanyId),
            pubCtx => pubCtx.CorrelationId = jobId, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("QA finalize: published AggregatesReadyEvent for {AnalysisJobId}", jobId);
        return Accepted(new { published = "AggregatesReadyEvent" });
    }
}
