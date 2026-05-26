using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Metrics.AverageRating;
using Obratka.Modules.Analytics.Metrics.ReviewCount;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Dashboards;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/analyses/{jobId:guid}/metrics")]
public sealed class DashboardMetricsController(
    WebApiDbContext db,
    IProcessingGatewayClient gateway,
    UserManager<ApplicationUser> userManager,
    IReviewCountMetricService? reviewCountService = null,
    IAverageRatingMetricService? averageRatingService = null) : ControllerBase
{
    // Метрика 1 «Количество отзывов» (per-branch) и О1 «Всего отзывов по сети»
    // (мульти-branch) — оба используют этот endpoint. Разница только в branchIds:
    //   М1 → ?branchIds=<один-guid>
    //   О1 → ?branchIds=<guid>,<guid>,...
    // Параметры:
    //  - branchIds: обязательный непустой CSV из guid'ов.
    //  - from / to: фильтр периода. Если хоть один null — тренд считается false.
    //  - sentiments / stars: 0..N значений (CSV). Пусто = «фильтр не применять».
    // sources в карточке не фильтрует строки (всегда 3 источника), но влияет на
    // total — на этом этапе фронт суммирует сам, бэкенд просто отдаёт per-source.
    [HttpGet("review-count")]
    [ProducesResponseType(typeof(ReviewCountMetricDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ReviewCountMetricDto>> ReviewCount(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sentiments,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (reviewCountService is null)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Analytics не сконфигурирован",
                detail: "ConnectionStrings:ProcessingReadDb пустой — Analytics-модуль не подключён к processing_db. Задай connection string и перезапусти Web API.");
        }

        var branchList = ParseGuids(branchIds);
        if (branchList is null || branchList.Count == 0)
            return BadRequest(new { error = "branchIds is required (CSV of guids, at least one)" });

        // Ownership check: тот же паттерн, что в AnalysesController — не палим
        // существование чужого джоба.
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        var sentimentList = ParseCsv(sentiments);
        var starsList = ParseStars(stars);

        var result = await reviewCountService.ComputeAsync(
            new ReviewCountMetricQuery(
                JobId: jobId,
                BranchIds: branchList,
                From: from,
                To: to,
                Sentiments: sentimentList,
                Stars: starsList),
            ct);

        var dto = new ReviewCountMetricDto(
            TotalCurrent: result.TotalCurrent,
            TotalPrevious: result.TotalPrevious,
            HasPreviousPeriod: result.HasPreviousPeriod,
            BySource: result.BySource
                .Select(kv => new ReviewCountSourceDto(kv.Key, kv.Value.Current, kv.Value.Previous))
                .ToList());
        return Ok(dto);
    }

    // Метрика 2 «Средний рейтинг» (per-branch). Те же параметры что у М1
    // (review-count), но возвращает средние по 3 источникам + общий weighted-avg.
    // Тренда к prev-периоду по спеке нет.
    [HttpGet("average-rating")]
    [ProducesResponseType(typeof(AverageRatingMetricDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AverageRatingMetricDto>> AverageRating(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sentiments,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (averageRatingService is null)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Analytics не сконфигурирован",
                detail: "ConnectionStrings:ProcessingReadDb пустой — Analytics-модуль не подключён к processing_db.");
        }

        var branchList = ParseGuids(branchIds);
        if (branchList is null || branchList.Count == 0)
            return BadRequest(new { error = "branchIds is required (CSV of guids, at least one)" });

        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        var result = await averageRatingService.ComputeAsync(
            new AverageRatingMetricQuery(
                JobId: jobId,
                BranchIds: branchList,
                From: from,
                To: to,
                Sentiments: ParseCsv(sentiments),
                Stars: ParseStars(stars)),
            ct);

        var dto = new AverageRatingMetricDto(
            TotalAverage: result.TotalAverage,
            TotalCount: result.TotalCount,
            BySource: result.BySource
                .Select(kv => new AverageRatingSourceDto(kv.Key, kv.Value.Average, kv.Value.Count))
                .ToList());
        return Ok(dto);
    }

    private static IReadOnlyCollection<Guid>? ParseGuids(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<Guid>(items.Length);
        foreach (var s in items)
        {
            if (Guid.TryParse(s, out var g)) list.Add(g);
        }
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyCollection<string>? ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? null : items;
    }

    private static IReadOnlyCollection<short>? ParseStars(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<short>(items.Length);
        foreach (var s in items)
        {
            if (short.TryParse(s, out var n) && n >= 1 && n <= 5)
                list.Add(n);
        }
        return list.Count == 0 ? null : list;
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }
}
