using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Monitoring;
using Obratka.Modules.Analytics.Recommendations;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;
using Obratka.WebApi.Monitoring;
using Obratka.WebApi.Scheduling;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/monitorings")]
public sealed class MonitoringsController(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    IMonitoringScheduler scheduler,
    UserManager<ApplicationUser> userManager,
    ILogger<MonitoringsController> logger,
    IMonitoringStatsService? stats = null,
    IRecommendationsService? recommendations = null) : ControllerBase
{
    // Окно в настройках убрано из UX (период выбирается на дашборде). Колонку оставляем
    // для совместимости со схемой, заполняем дефолтом.
    private const int DefaultWindowDays = 30;

    // Включение мониторинга с дашборда: создаём config (active), снимаем baseline (cycle 0),
    // регистрируем recurring-job и стартуем первый цикл (ТЗ §1).
    [HttpPost]
    [ProducesResponseType(typeof(CreateMonitoringResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateMonitoringResponse>> Create(
        [FromBody] CreateMonitoringRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var company = await db.Companies.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == request.CompanyId && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound(new { error = "Company not found" });

        if (!TryValidateSettings(request.Sources, request.BranchIds, request.Frequency,
                out var frequency, out var validationError))
            return BadRequest(new { error = validationError });

        // Seed job должен существовать и принадлежать этой компании.
        AnalysisJobDto? job;
        try
        {
            job = await gateway.GetAnalysisAsync(request.SeedJobId, ct);
        }
        catch (Exception ex) when (ex is ProcessingGatewayException or HttpRequestException)
        {
            logger.LogWarning(ex, "PG недоступен при создании мониторинга для job {JobId}", request.SeedJobId);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Сервис обработки недоступен",
                detail: "Не удалось проверить анализ в Processing Gateway. Попробуйте позже.");
        }
        if (job is null || job.CompanyId != request.CompanyId)
            return NotFound(new { error = "Анализ не найден или не принадлежит этой компании." });

        // Один мониторинг на seed-job (модель «растущий job»).
        var exists = await db.MonitoringConfigs.AnyAsync(m => m.SeedJobId == request.SeedJobId, ct);
        if (exists)
            return Conflict(new { error = "Для этого анализа мониторинг уже включён." });

        var config = new MonitoringConfig
        {
            CompanyId = request.CompanyId,
            UserId = ownerId.Value,
            SeedJobId = request.SeedJobId,
            Sources = request.Sources.Distinct().ToList(),
            BranchIds = request.BranchIds.Distinct().ToList(),
            WindowDays = DefaultWindowDays,
            Frequency = frequency,
            CronSchedule = MonitoringFrequencies.ToCron(frequency),
            Status = MonitoringStatus.Active,
        };
        db.MonitoringConfigs.Add(config);

        // Baseline cycle 0 — снимок текущего состояния разового анализа (без сбора).
        var baseline = new MonitoringCycle
        {
            MonitoringId = config.Id,
            CycleNumber = 0,
            StartedAt = config.CreatedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            Status = MonitoringCycleStatus.Success,
            PeriodFrom = null,
            PeriodTo = config.CreatedAt,
            SummarySnapshot = job.Summary,
        };
        if (stats is not null)
        {
            try
            {
                var s = await stats.ComputeCycleStatsAsync(config.SeedJobId, DateTimeOffset.MinValue, ct);
                baseline.TotalReviewsAtCycle = s.TotalReviews;
                baseline.NegativeRatioPp = s.NegativeRatioPp; // кумулятивная доля для справки
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Baseline stats failed for monitoring on job {JobId}", request.SeedJobId);
            }
        }
        if (recommendations is not null)
        {
            try
            {
                var list = await recommendations.ListByJobAsync(config.SeedJobId, ct);
                baseline.RecommendationsSnapshot = list
                    .Select(r => new RecommendationSnapshotItem
                    {
                        Priority = r.Priority,
                        Topic = r.Topic,
                        Title = r.Title,
                        Body = r.Body,
                        ExpectedImpact = r.ExpectedImpact,
                        Evidence = r.Evidence.ToList(),
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Baseline recommendations snapshot failed for job {JobId}", request.SeedJobId);
            }
        }
        db.MonitoringCycles.Add(baseline);

        await db.SaveChangesAsync(ct);

        scheduler.Register(config);
        scheduler.EnqueueNow(config.Id); // первый цикл сразу

        logger.LogInformation("Monitoring {Id} created for job {JobId} (freq={Freq}, window={Window}d)",
            config.Id, config.SeedJobId, config.Frequency, config.WindowDays);

        return CreatedAtAction(nameof(Get), new { id = config.Id }, new CreateMonitoringResponse(config.Id));
    }

    // Список мониторингов пользователя.
    [HttpGet]
    [ProducesResponseType(typeof(MonitoringListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MonitoringListResponse>> List(CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var configs = await db.MonitoringConfigs.AsNoTracking()
            .Where(m => m.UserId == ownerId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        var items = await MapListItemsAsync(configs, ct);
        return Ok(new MonitoringListResponse(items));
    }

    // Деталь: конфиг + история циклов (timeline + снапшоты рекомендаций).
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(MonitoringDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MonitoringDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var config = await db.MonitoringConfigs.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == id && m.UserId == ownerId, ct);
        if (config is null) return NotFound();

        var item = (await MapListItemsAsync([config], ct)).Single();

        var cycles = await db.MonitoringCycles.AsNoTracking()
            .Where(c => c.MonitoringId == id)
            .OrderByDescending(c => c.CycleNumber)
            .ToListAsync(ct);

        var cycleDtos = cycles.Select(c => new MonitoringCycleDto(
            c.CycleNumber,
            c.StartedAt,
            c.FinishedAt,
            c.Status.Wire(),
            c.PeriodFrom,
            c.PeriodTo,
            c.NewReviewCount,
            c.TotalReviewsAtCycle,
            c.NegativeRatioPp,
            c.NegativeSpikeTriggered,
            c.SummarySnapshot,
            c.RecommendationsSnapshot.Select(r => new RecommendationSnapshotDto(
                r.Priority, r.Topic, r.Title, r.Body, r.ExpectedImpact, r.Evidence)).ToList(),
            c.Error)).ToList();

        return Ok(new MonitoringDetailDto(item, cycleDtos));
    }

    // Изменить настройки (источники/филиалы/окно/частота) → перерегистрируем job.
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(MonitoringListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MonitoringListItemDto>> Update(
        Guid id, [FromBody] UpdateMonitoringRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var config = await db.MonitoringConfigs
            .SingleOrDefaultAsync(m => m.Id == id && m.UserId == ownerId, ct);
        if (config is null) return NotFound();

        if (!TryValidateSettings(request.Sources, request.BranchIds, request.Frequency,
                out var frequency, out var validationError))
            return BadRequest(new { error = validationError });

        config.Sources = request.Sources.Distinct().ToList();
        config.BranchIds = request.BranchIds.Distinct().ToList();
        config.Frequency = frequency;
        config.CronSchedule = MonitoringFrequencies.ToCron(frequency);
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Перерегистрируем только активный; на паузе job снят и встанет при resume.
        if (config.Status == MonitoringStatus.Active)
            scheduler.Register(config);

        var item = (await MapListItemsAsync([config], ct)).Single();
        return Ok(item);
    }

    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct)
    {
        var config = await GetOwnedAsync(id, ct);
        if (config is null) return NotFound();

        config.Status = MonitoringStatus.Paused;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        scheduler.Remove(id);
        return NoContent();
    }

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
    {
        var config = await GetOwnedAsync(id, ct);
        if (config is null) return NotFound();

        config.Status = MonitoringStatus.Active;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        scheduler.Register(config);
        return NoContent();
    }

    // «Обновить вручную» — досрочный запуск цикла.
    [HttpPost("{id:guid}/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct)
    {
        var config = await GetOwnedAsync(id, ct);
        if (config is null) return NotFound();
        if (config.Status == MonitoringStatus.Paused)
            return Conflict(new { error = "Мониторинг на паузе — возобновите перед ручным запуском." });

        scheduler.EnqueueNow(id);
        return Accepted();
    }

    // Отключить: удаляем config (+ cycles каскадом). Отзывы и анализ в processing_db остаются (ТЗ).
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var config = await GetOwnedAsync(id, ct);
        if (config is null) return NotFound();

        scheduler.Remove(id);
        db.MonitoringConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<MonitoringConfig?> GetOwnedAsync(Guid id, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return null;
        return await db.MonitoringConfigs
            .SingleOrDefaultAsync(m => m.Id == id && m.UserId == ownerId, ct);
    }

    private async Task<List<MonitoringListItemDto>> MapListItemsAsync(
        IReadOnlyList<MonitoringConfig> configs, CancellationToken ct)
    {
        if (configs.Count == 0) return [];

        var companyIds = configs.Select(c => c.CompanyId).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        var companyById = companies.ToDictionary(c => c.Id, c => c.Name);

        var branchIds = configs.SelectMany(c => c.BranchIds).Distinct().ToList();
        var branches = await db.LogicalBranches.AsNoTracking()
            .Where(lb => branchIds.Contains(lb.Id))
            .Select(lb => new { lb.Id, lb.Name, lb.Address, lb.City })
            .ToListAsync(ct);
        var branchById = branches.ToDictionary(b => b.Id);

        return configs.Select(c => new MonitoringListItemDto(
            c.Id,
            c.CompanyId,
            companyById.TryGetValue(c.CompanyId, out var name) ? name : "—",
            c.SeedJobId,
            c.Sources,
            c.BranchIds.Select(id =>
            {
                branchById.TryGetValue(id, out var b);
                return new MonitoringBranchDto(id, b?.Name, b?.Address, b?.City);
            }).ToList(),
            c.WindowDays,
            c.Frequency.ToString(),
            c.Status.Wire(),
            c.LastCollectedAt,
            c.LastRunStatus?.Wire(),
            c.CreatedAt)).ToList();
    }

    private bool TryValidateSettings(
        List<string> sources, List<Guid> branchIds, string frequencyRaw,
        out MonitoringFrequency frequency, out string? error)
    {
        frequency = default;
        error = null;

        if (sources is null || sources.Count == 0)
        {
            error = "Выберите хотя бы один источник.";
            return false;
        }
        if (branchIds is null || branchIds.Count == 0)
        {
            error = "Выберите хотя бы один филиал.";
            return false;
        }
        if (!Enum.TryParse(frequencyRaw, ignoreCase: true, out frequency))
        {
            error = $"Неизвестная частота: {frequencyRaw}.";
            return false;
        }

        var isAdmin = User.IsInRole(Roles.Admin);
        if (!MonitoringFrequencies.IsAllowedForRole(frequency, isAdmin))
        {
            error = isAdmin
                ? "Недопустимая частота."
                : "Эта частота доступна только администратору. Выберите: сутки, неделя, 2 недели или месяц.";
            return false;
        }

        return true;
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }
}
