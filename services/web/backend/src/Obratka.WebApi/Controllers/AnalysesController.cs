using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Analyses;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/analyses")]
public sealed class AnalysesController(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    UserManager<ApplicationUser> userManager,
    ILogger<AnalysesController> logger) : ControllerBase
{
    // Запуск анализа из мастера (step 3 "Запустить"). Web API сам разбирает группировку
    // из БД и формирует payload для PG, проставляя BranchId=LogicalBranch.Id (физический
    // филиал) во все таргеты — см. обсуждение по ADR-003: в reviews.branch_id ложится
    // физический id, аналитика разрезает по нему без cross-DB JOIN'ов.
    [HttpPost("start")]
    [ProducesResponseType(typeof(StartAnalysisResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StartAnalysisResponse>> Start(
        [FromBody] StartAnalysisRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var company = await db.Companies.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == request.CompanyId && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound(new { error = "Company not found" });

        if (request.PeriodFrom is not null && request.PeriodTo is not null
            && request.PeriodFrom > request.PeriodTo)
            return BadRequest(new { error = "PeriodFrom must be <= PeriodTo" });

        // Грузим активные физ. филиалы вместе с их активными провайдерскими карточками.
        // Активная карточка = (LogicalBranch.IsSelected=true И CompanyBranch.IsSelected=true)
        // плюс непустые externalId/externalUrl (parser без них не отработает).
        var logicalBranches = await db.LogicalBranches.AsNoTracking()
            .Where(lb => lb.CompanyId == request.CompanyId && lb.IsSelected)
            .Select(lb => new
            {
                lb.Id,
                Cards = db.CompanyBranches
                    .Where(b => b.LogicalBranchId == lb.Id
                                && b.IsSelected
                                && b.ExternalId != ""
                                && b.ExternalUrl != null)
                    .Select(b => new { b.Source, b.ExternalId, ExternalUrl = b.ExternalUrl! })
                    .ToList(),
            })
            .ToListAsync(ct);

        var specs = logicalBranches
            .SelectMany(lb => lb.Cards.Select(c =>
                new StartAnalysisBranchSpec(lb.Id, c.Source, c.ExternalId, c.ExternalUrl)))
            .ToList();

        if (specs.Count == 0)
            return BadRequest(new
            {
                error = "В этой компании нет ни одного включённого филиала с валидной карточкой. " +
                        "Вернитесь на шаг 2 и проверьте группировку.",
            });

        StartAnalysisQaResponse pgResponse;
        try
        {
            pgResponse = await gateway.StartAnalysisAsync(
                new StartAnalysisQaRequest(
                    request.CompanyId, request.PeriodFrom, request.PeriodTo, specs),
                ct);
        }
        catch (ProcessingGatewayException ex)
        {
            logger.LogWarning(ex,
                "Processing Gateway отклонил запуск анализа компании {CompanyId}: {Status}",
                request.CompanyId, ex.StatusCode);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Сервис обработки сейчас недоступен",
                detail: "Processing Gateway вернул ошибку при попытке запустить анализ. Попробуйте ещё раз через минуту.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Processing Gateway недоступен для запуска анализа компании {CompanyId}", request.CompanyId);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Сервис обработки сейчас недоступен",
                detail: "Не удалось связаться с Processing Gateway. Проверьте, что стенд поднят.");
        }

        var location = $"/api/analyses/{pgResponse.AnalysisJobId}";
        return Accepted(location, new StartAnalysisResponse(pgResponse.AnalysisJobId));
    }

    // Список анализов текущего пользователя по всем его компаниям. Опц. фильтр companyId.
    // Лимит применяется по каждой компании в отдельности (для MVP допустимо: юзер редко
    // имеет >5 компаний). Если параметр companyId передан и принадлежит юзеру — берём только её.
    [HttpGet]
    [ProducesResponseType(typeof(AnalysisJobListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisJobListResponse>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? companyId,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        List<Guid> companyIds;
        if (companyId is not null)
        {
            var owns = await db.Companies
                .AnyAsync(c => c.Id == companyId && c.OwnerUserId == ownerId, ct);
            if (!owns) return Ok(new AnalysisJobListResponse(0, limit ?? 50, offset ?? 0, []));
            companyIds = [companyId.Value];
        }
        else
        {
            companyIds = await db.Companies.AsNoTracking()
                .Where(c => c.OwnerUserId == ownerId)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        if (companyIds.Count == 0)
            return Ok(new AnalysisJobListResponse(0, limit ?? 50, offset ?? 0, []));

        var effectiveLimit = limit ?? 50;
        var effectiveOffset = offset ?? 0;

        // N запросов параллельно — на MVP норм. Когда понадобится — расширим PG, чтобы
        // принимал массив companyIds, либо вынесем listing в analytics_reader (но это пока не нужно).
        var lists = await Task.WhenAll(
            companyIds.Select(cid => gateway.ListAnalysesAsync(status, cid, effectiveLimit, 0, ct)));

        var allItems = lists.SelectMany(r => r.Items)
            .OrderByDescending(i => i.CreatedAt)
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToList();
        var total = lists.Sum(r => r.Total);

        return Ok(new AnalysisJobListResponse(total, effectiveLimit, effectiveOffset, allItems));
    }

    // Деталь job-а. Возвращаем 404 если job не принадлежит юзеру — не палим существование.
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(AnalysisJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisJobDto>> Get(Guid jobId, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();

        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        return Ok(job);
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }
}
