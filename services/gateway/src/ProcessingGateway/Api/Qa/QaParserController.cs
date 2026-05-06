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
    /// Job должен быть в `collecting` (иначе ParserPoller не подхватит).
    /// Использование: парсер-таск завис, реальный пользователь хочет ускорить.
    [HttpPost("restart-source/{jobId:guid}/{source}")]
    public async Task<IActionResult> RestartSource(
        Guid jobId,
        string source,
        [FromBody] RestartSourceRequest request,
        CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();
        if (job.Status != AnalysisJobStatus.Collecting)
            return Conflict(new { error = $"Job in status {job.Status.ToWire()}, restart допустим только в collecting" });
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
        var progress = new Dictionary<string, CollectionProgressEntry>(job.CollectionProgress);
        progress[source] = new CollectionProgressEntry
        {
            TaskId = taskId,
            Status = "pending",
            Progress = 0
        };
        job.CollectionProgress = progress;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA parser/restart-source: job={AnalysisJobId} source={Source} new task_id={TaskId}",
            jobId, source, taskId);
        return Accepted(new { source, task_id = taskId });
    }

    public sealed record RestartSourceRequest(
        IReadOnlyList<BranchTargetDto> Branches,
        DateTimeOffset? DateFrom,
        DateTimeOffset? DateTo);
}
