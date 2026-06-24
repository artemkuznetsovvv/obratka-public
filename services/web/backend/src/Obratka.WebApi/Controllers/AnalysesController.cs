using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Recommendations;
using Obratka.Modules.Notifications;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Analyses;
using Obratka.WebApi.Notifications;
using Obratka.WebApi.Contracts.Dashboards;
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
    ILogger<AnalysesController> logger,
    INotificationsModule notifications,
    IRecommendationsService? recommendationsService = null) : ControllerBase
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

        // Для request-summary (EnrichDiagnosticContext) и сквозного трейса.
        HttpContext.Items["CompanyId"] = request.CompanyId;

        var company = await db.Companies.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == request.CompanyId && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound(new { error = "Компания не найдена" });

        if (request.PeriodFrom is not null && request.PeriodTo is not null
            && request.PeriodFrom > request.PeriodTo)
            return BadRequest(new { error = "Дата начала периода должна быть не позже даты окончания" });

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
                    request.CompanyId, request.PeriodFrom, request.PeriodTo, specs,
                    // Бизнес-контекст из формы нового анализа (хранится на Company) —
                    // прокидываем в PG, чтобы тот вложил его в input.json для LLM.
                    BusinessCategory: company.Category,
                    BusinessSubcategory: company.Subcategory,
                    AdditionalContext: company.Description),
                ct);
        }
        catch (ProcessingGatewayException ex)
        {
            logger.LogWarning(ex,
                "Processing Gateway отклонил запуск анализа компании {CompanyId}: {Status}",
                request.CompanyId, ex.StatusCode);
            await SafeAdminAlertAsync("Анализ",
                $"PG отклонил запуск анализа ({(int)ex.StatusCode}): {ex.Message}",
                ownerId.Value, request.CompanyId, company.Name, null);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Сервис обработки сейчас недоступен",
                detail: "Processing Gateway вернул ошибку при попытке запустить анализ. Попробуйте ещё раз через минуту.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Processing Gateway недоступен для запуска анализа компании {CompanyId}", request.CompanyId);
            await SafeAdminAlertAsync("Анализ",
                $"PG недоступен при запуске анализа: {ex.Message}",
                ownerId.Value, request.CompanyId, company.Name, null);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Сервис обработки сейчас недоступен",
                detail: "Не удалось связаться с Processing Gateway. Проверьте, что стенд поднят.");
        }

        // Трекер для уведомления по готовности (фоновая reconcile-джоба отследит завершение).
        db.AnalysisNotifications.Add(new AnalysisNotification
        {
            JobId = pgResponse.AnalysisJobId,
            UserId = ownerId.Value,
            CompanyId = request.CompanyId,
        });
        await db.SaveChangesAsync(ct);

        // Web-API-сторона pivot: связывает CorrelationId этого запроса с новым AnalysisJobId,
        // который дальше станет сквозным трейсом анализа через PG/Parser. Initiator (user:<id>)
        // уже в LogContext из InitiatorEnrichmentMiddleware — не дублируем в аргументах.
        HttpContext.Items["AnalysisJobId"] = pgResponse.AnalysisJobId;
        logger.LogInformation(
            "Analysis started for company {CompanyId} -> job {AnalysisJobId}",
            request.CompanyId, pgResponse.AnalysisJobId);

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

    // «Сколько отзывов собрано по конкретному филиалу × источнику» для блока «Результаты»
    // на детальной странице. PG отдаёт чистые counts (branch_id + source + count), мы
    // подмешиваем имя/адрес LogicalBranch из webapi_db чтобы фронт мог человеческие лейблы
    // показать. LogicalBranch мог быть удалён юзером после запуска — тогда BranchName = null.
    [HttpGet("{jobId:guid}/branch-stats")]
    [ProducesResponseType(typeof(IReadOnlyList<BranchStatsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<BranchStatsDto>>> BranchStats(
        Guid jobId, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        IReadOnlyList<BranchStatsItem> stats;
        try
        {
            stats = await gateway.GetBranchStatsAsync(jobId, ct);
        }
        catch (ProcessingGatewayException ex)
        {
            logger.LogWarning(ex, "Processing Gateway вернул ошибку на branch-stats для {JobId}", jobId);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Не удалось получить статистику",
                detail: "Processing Gateway не отдал статистику по филиалам.");
        }

        if (stats.Count == 0) return Ok(Array.Empty<BranchStatsDto>());

        var branchIds = stats.Select(s => s.BranchId).Distinct().ToHashSet();
        var branches = await db.LogicalBranches.AsNoTracking()
            .Where(lb => lb.CompanyId == job.CompanyId && branchIds.Contains(lb.Id))
            .Select(lb => new { lb.Id, lb.Name, lb.Address })
            .ToListAsync(ct);
        var byId = branches.ToDictionary(b => b.Id);

        var dto = stats
            .Select(s =>
            {
                byId.TryGetValue(s.BranchId, out var info);
                return new BranchStatsDto(
                    s.BranchId, info?.Name, info?.Address, s.Source, s.ReviewCount);
            })
            .ToList();
        return Ok(dto);
    }

    // Шапка дашборда для конкретного завершённого/частично завершённого анализа.
    // Phase 0: возвращаем только хедер (компания, branches, sources, статус, счётчики).
    // Метрики (О1-О3, 1-7) добавятся позже отдельными полями/методами.
    // Period берём из company.draftPeriodFrom/to — это caveat: фактический период
    // джоба в processing_db не хранится (processing-gateway-todo.md #1).
    [HttpGet("{jobId:guid}/dashboard")]
    [ProducesResponseType(typeof(DashboardHeaderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<DashboardHeaderDto>> Dashboard(
        Guid jobId, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == job.CompanyId && c.OwnerUserId == ownerId)
            .Select(c => new { c.Id, c.Name, c.DraftPeriodFrom, c.DraftPeriodTo })
            .SingleOrDefaultAsync(ct);
        if (company is null) return NotFound();

        // Список филиалов джоба — distinct(branch_id) из branch-stats (PG уже отдаёт
        // готовый агрегат по analysis_job_reviews × reviews). Имена и адреса джойним
        // из webapi_db.LogicalBranches; удалённый филиал → name=null (UI покажет
        // placeholder, как в HistoryDetailPage).
        IReadOnlyList<BranchStatsItem> stats;
        try
        {
            stats = await gateway.GetBranchStatsAsync(jobId, ct);
        }
        catch (ProcessingGatewayException ex)
        {
            logger.LogWarning(ex, "PG вернул ошибку на branch-stats для dashboard {JobId}", jobId);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Не удалось получить данные дашборда",
                detail: "Processing Gateway не отдал список филиалов для анализа.");
        }

        var branchIds = stats.Select(s => s.BranchId).Distinct().ToList();
        var branchInfos = await db.LogicalBranches.AsNoTracking()
            .Where(lb => lb.CompanyId == job.CompanyId && branchIds.Contains(lb.Id))
            .Select(lb => new { lb.Id, lb.Name, lb.Address, lb.City })
            .ToListAsync(ct);
        var byId = branchInfos.ToDictionary(b => b.Id);

        var branches = branchIds
            .Select(id =>
            {
                byId.TryGetValue(id, out var info);
                return new DashboardBranchDto(id, info?.Name, info?.Address, info?.City);
            })
            .ToList();

        // sources — ключи collection_progress (PG заполняет один ключ на каждый
        // источник, по которому шёл сбор; формат фиксирован — slug-и 2gis/yandex/google).
        var sources = job.CollectionProgress.Keys
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return Ok(new DashboardHeaderDto(
            JobId: job.Id,
            CompanyId: company.Id,
            CompanyName: company.Name,
            Branches: branches,
            Sources: sources,
            Status: job.Status,
            ReviewCount: job.ReviewCount,
            RecommendationsCount: job.RecommendationsCount,
            CreatedAt: job.CreatedAt,
            CompletedAt: job.CompletedAt,
            PeriodFrom: company.DraftPeriodFrom,
            PeriodTo: company.DraftPeriodTo));
    }

    // Список LLM-рекомендаций по результатам анализа. Сортировка sort_order ASC.
    // Если Analytics-модуль не сконфигурирован (пустой ProcessingReadDb cs) —
    // вернёт 503; для UI это значит «не показывать блок».
    [HttpGet("{jobId:guid}/recommendations")]
    [ProducesResponseType(typeof(RecommendationListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RecommendationListDto>> Recommendations(
        Guid jobId, CancellationToken ct)
    {
        if (recommendationsService is null)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Analytics не сконфигурирован",
                detail: "ConnectionStrings:ProcessingReadDb пустой — рекомендации недоступны.");
        }

        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        var rows = await recommendationsService.ListByJobAsync(jobId, ct);
        var items = rows
            .Select(r => new RecommendationDto(
                r.Id, r.Priority, r.Topic, r.Title, r.Body, r.ExpectedImpact, r.Evidence))
            .ToList();
        return Ok(new RecommendationListDto(items));
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }

    // Синхронный admin-алерт при сбое ЗАПУСКА анализа (PG отклонил/недоступен). Async-исходы
    // (completed/partial/failed) обрабатывает фоновая AnalysisNotificationReconciler.
    private async Task SafeAdminAlertAsync(
        string stage, string reason, Guid? userId, Guid? companyId, string? companyName, Guid? jobId,
        string severity = "critical")
    {
        try
        {
            await notifications.SendAdminAlertAsync(new AdminAlert(
                Stage: stage,
                Reason: reason,
                Severity: severity,
                EventId: Guid.NewGuid().ToString("N"),
                UserId: userId,
                CompanyId: companyId,
                CompanyName: companyName,
                JobId: jobId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin alert send failed (stage={Stage}, company={CompanyId})", stage, companyId);
        }
    }
}
