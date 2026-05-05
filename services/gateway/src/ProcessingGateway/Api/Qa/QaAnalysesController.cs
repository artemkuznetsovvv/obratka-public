using MassTransit;
using Microsoft.AspNetCore.Mvc;
using ProcessingGateway.Api;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Infrastructure.Database;

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
        var cmd = new StartAnalysisCommand(
            AnalysisJobId: jobId,
            CompanyId: request.CompanyId,
            DateFrom: request.DateFrom,
            DateTo: request.DateTo,
            Branches: request.Branches);

        await _publisher.Publish(cmd, ct);

        // MassTransit EF Outbox: сообщение шипится в брокер только после
        // DbContext.SaveChangesAsync(). Без этого вызова publish осядет в outbox-таблице
        // и никогда не уйдёт. SaveChanges на пустом DbContext безвреден — это просто
        // commit «нет изменений».
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "QA published StartAnalysisCommand: job={AnalysisJobId} branches={BranchCount}",
            jobId, request.Branches.Count);

        return AcceptedAtAction(
            actionName: null,
            value: new StartAnalysisQaResponse(jobId));
    }
}
