using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Parser;
using ProcessingGateway.Infrastructure.Parser.Contracts;

namespace ProcessingGateway.Api.Qa;

/// QA-эндпоинты parser-стороны: пинг + перезапуск конкретного источника
/// внутри job-а (если task завис/упал, не трогая остальные).
[ApiController]
[Route("api/qa/parser")]
[RequireQaApiKey]
public sealed class QaParserController : ControllerBase
{
    private readonly IParserClient _parser;
    private readonly ProcessingDbContext _db;
    private readonly ILogger<QaParserController> _logger;

    public QaParserController(IParserClient parser, ProcessingDbContext db, ILogger<QaParserController> logger)
    {
        _parser = parser;
        _db = db;
        _logger = logger;
    }

    [HttpGet("ping")]
    public async Task<IActionResult> Ping(CancellationToken ct)
    {
        // Простейшая проверка: GET по несуществующему task_id ждёт 404.
        // Если получили что-то иное (например, 5xx или сетевую ошибку) — Parser нездоров.
        try
        {
            await _parser.GetStatusAsync(Guid.NewGuid(), ct);
            return Ok(new { ok = true, note = "Parser неожиданно вернул 200 на random task_id" });
        }
        catch (ParserTaskNotFoundException)
        {
            return Ok(new { ok = true, note = "Parser ответил 404 на несуществующий task — норма" });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { ok = false, error = ex.Message });
        }
    }

    /// Перезапустить сбор по одному источнику в рамках уже существующего job-а.
    /// Допустимые исходные статусы: `collecting`, `completed`, `partial`, `failed` —
    /// то есть либо активный сбор, либо терминальные состояния, из которых можно
    /// «отмотать» job назад. Из `sent_to_llm`/`computing_aggregates` рестарт запрещён —
    /// pipeline активно работает и сброс может потерять in-flight состояние.
    ///
    /// При рестарте из терминального статуса job возвращается в `collecting`
    /// (чтобы `ParserPoller` подхватил новую таску); по завершении сбора
    /// `AnalysisOrchestrator` снова дойдёт до LLM и перепишет результаты.
    /// `completed_at`/`error` сбрасываются.
    [HttpPost("restart-source/{jobId:guid}/{source}")]
    public async Task<IActionResult> RestartSource(
        Guid jobId,
        string source,
        [FromBody] RestartSourceRequest request,
        CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        var restartable = job.Status
            is AnalysisJobStatus.Collecting
            or AnalysisJobStatus.Completed
            or AnalysisJobStatus.Partial
            or AnalysisJobStatus.Failed;
        if (!restartable)
            return Conflict(new
            {
                error = $"Job in status {job.Status.ToWire()}, restart допустим только из collecting/completed/partial/failed"
            });
        if (request.Branches is null || request.Branches.Count == 0)
            return BadRequest(new { error = "branches required" });

        var taskId = await _parser.StartCollectionAsync(new StartCollectionRequest(
            JobId: jobId,
            CompanyId: job.CompanyId,
            Source: source,
            DateFrom: request.DateFrom,
            DateTo: request.DateTo,
            Branches: request.Branches), ct);

        // Замещаем существующую запись в collection_progress[source].
        // StartedAt = NOW — иначе ParserPoller отсчитает таймаут от job.CreatedAt
        // и сразу пометит новую таску failed на старом job-е.
        var progress = new Dictionary<string, CollectionProgressEntry>(job.CollectionProgress);
        progress[source] = new CollectionProgressEntry
        {
            TaskId = taskId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "pending",
            Progress = 0
        };
        job.CollectionProgress = progress;

        // Если job был в терминальном состоянии — откатываем в collecting, чтобы поллер подхватил.
        var previousStatus = job.Status;
        if (previousStatus != AnalysisJobStatus.Collecting)
        {
            job.Status = AnalysisJobStatus.Collecting;
            job.CompletedAt = null;
            job.Error = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA parser/restart-source: job={AnalysisJobId} source={Source} new task_id={TaskId} " +
            "previous_status={PreviousStatus}",
            jobId, source, taskId, previousStatus.ToWire());
        return Accepted(new
        {
            source,
            task_id = taskId,
            previous_status = previousStatus.ToWire(),
            current_status = job.Status.ToWire()
        });
    }

    /// Перезапустить сбор по ВСЕМ переданным источникам job-а одним вызовом — вход для цикла
    /// live-мониторинга («растущий seed-job»). В отличие от restart-source:
    ///   • guard от перекрытия циклов: если job уже `collecting` → 409 (предыдущий цикл ещё идёт);
    ///   • допустимые исходные статусы — только терминальные (completed/partial/failed);
    ///   • стартует источники одной операцией, изоляция сбоев per-source (ADR-001 §10): упавший
    ///     на старте источник помечается failed, остальные продолжают → финал partial.
    [HttpPost("restart-all/{jobId:guid}")]
    public async Task<IActionResult> RestartAll(
        Guid jobId,
        [FromBody] RestartAllRequest request,
        CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        // Guard #4: цикл уже идёт — не перезапускаем, чтобы не осиротить in-flight таски.
        if (job.Status == AnalysisJobStatus.Collecting)
            return Conflict(new { error = "Job уже в collecting — цикл сбора в процессе" });

        var restartable = job.Status
            is AnalysisJobStatus.Completed
            or AnalysisJobStatus.Partial
            or AnalysisJobStatus.Failed;
        if (!restartable)
            return Conflict(new
            {
                error = $"Job in status {job.Status.ToWire()}, restart-all допустим только из completed/partial/failed"
            });

        if (request.Sources is null || request.Sources.Count == 0)
            return BadRequest(new { error = "sources required" });
        if (request.Sources.Any(s => s.Branches is null || s.Branches.Count == 0))
            return BadRequest(new { error = "each source must contain at least one branch" });

        var now = DateTimeOffset.UtcNow;
        var progress = new Dictionary<string, CollectionProgressEntry>(job.CollectionProgress);
        var results = new List<object>();

        foreach (var src in request.Sources)
        {
            try
            {
                var taskId = await _parser.StartCollectionAsync(new StartCollectionRequest(
                    JobId: jobId,
                    CompanyId: job.CompanyId,
                    Source: src.Source,
                    DateFrom: request.DateFrom,
                    DateTo: request.DateTo,
                    Branches: src.Branches), ct);

                progress[src.Source] = new CollectionProgressEntry
                {
                    TaskId = taskId,
                    StartedAt = now,
                    Status = "pending",
                    Progress = 0
                };
                results.Add(new { source = src.Source, task_id = taskId, started = true });
            }
            catch (Exception ex)
            {
                // Изоляция сбоев: источник не стартовал → failed, остальные продолжают.
                progress[src.Source] = new CollectionProgressEntry
                {
                    Status = "failed",
                    Error = $"restart failed to start: {ex.Message}"
                };
                results.Add(new { source = src.Source, task_id = (Guid?)null, started = false });
                _logger.LogWarning(ex,
                    "QA parser/restart-all: source {Source} failed to start for job {AnalysisJobId}",
                    src.Source, jobId);
            }
        }

        job.CollectionProgress = progress;

        // Откатываем job в collecting (был терминальный) — ParserPoller подхватит pending-таски,
        // по завершении AnalysisOrchestrator снова дойдёт до LLM и перепишет результаты.
        var previousStatus = job.Status;
        job.Status = AnalysisJobStatus.Collecting;
        job.CompletedAt = null;
        job.Error = null;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA parser/restart-all: job={AnalysisJobId} sources={Sources} previous_status={PreviousStatus}",
            jobId, string.Join(",", request.Sources.Select(s => s.Source)), previousStatus.ToWire());

        return Accepted(new
        {
            previous_status = previousStatus.ToWire(),
            current_status = job.Status.ToWire(),
            tasks = results
        });
    }

    public sealed record RestartSourceRequest(
        IReadOnlyList<BranchTargetDto> Branches,
        DateTimeOffset? DateFrom,
        DateTimeOffset? DateTo);

    public sealed record RestartAllRequest(
        IReadOnlyList<RestartAllSource> Sources,
        DateTimeOffset? DateFrom,
        DateTimeOffset? DateTo);

    public sealed record RestartAllSource(
        string Source,
        IReadOnlyList<BranchTargetDto> Branches);
}
