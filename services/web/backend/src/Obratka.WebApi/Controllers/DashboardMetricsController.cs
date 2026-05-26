using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Metrics.AverageRating;
using Obratka.Modules.Analytics.Metrics.FreshPulse;
using Obratka.Modules.Analytics.Metrics.ReviewCount;
using Obratka.Modules.Analytics.Metrics.SentimentDistribution;
using Obratka.Modules.Analytics.Metrics.TopTopics;
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
    IAverageRatingMetricService? averageRatingService = null,
    ISentimentDistributionMetricService? sentimentDistributionService = null,
    ISentimentReviewsService? sentimentReviewsService = null,
    IFreshPulseMetricService? freshPulseService = null,
    ITopTopicsMetricService? topTopicsService = null) : ControllerBase
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

    // Метрика 3 «Настроение клиентов» (per-branch) и О3 «Настроение по сети»
    // (мульти-branch). Возвращает counts по 3 sentiment-bucket'ам; фронт сам
    // считает проценты и применяет таблицу «фраза-вывод».
    //
    // ВАЖНО: фильтр sentiments сюда не принимаем — метрика сама показывает
    // разрез по sentiments (см. SentimentDistributionMetricService).
    [HttpGet("sentiment-distribution")]
    [ProducesResponseType(typeof(SentimentDistributionMetricDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SentimentDistributionMetricDto>> SentimentDistribution(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sources,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (sentimentDistributionService is null)
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

        var result = await sentimentDistributionService.ComputeAsync(
            new SentimentDistributionQuery(
                JobId: jobId,
                BranchIds: branchList,
                From: from,
                To: to,
                Sources: ParseCsv(sources),
                Stars: ParseStars(stars)),
            ct);

        return Ok(new SentimentDistributionMetricDto(
            Positive: result.Positive,
            Neutral: result.Neutral,
            Negative: result.Negative,
            TotalNonEmpty: result.TotalNonEmpty));
    }

    // Список отзывов конкретной тональности — для модалки раскрытия М3/О3.
    // ADR-003 разрешает одно raw-обращение к reviews × review_llm_results для
    // «топ-N» (см. §«Топ-N»). Сортировка ReviewDate DESC, limit/offset для
    // пагинации (limit ≤ 200, server-side clamp).
    [HttpGet("sentiment-reviews")]
    [ProducesResponseType(typeof(SentimentReviewsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SentimentReviewsDto>> SentimentReviews(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] string? sentiment,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sources,
        [FromQuery] string? stars,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        if (sentimentReviewsService is null)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Analytics не сконфигурирован",
                detail: "ConnectionStrings:ProcessingReadDb пустой — Analytics-модуль не подключён к processing_db.");
        }

        var branchList = ParseGuids(branchIds);
        if (branchList is null || branchList.Count == 0)
            return BadRequest(new { error = "branchIds is required (CSV of guids, at least one)" });

        if (string.IsNullOrWhiteSpace(sentiment))
            return BadRequest(new { error = "sentiment is required (one of: позитивный, нейтральный, негативный)" });

        // Защита от мусора в query — sentiment строго один из 3 schema 2.0 значений.
        if (sentiment is not "позитивный" and not "нейтральный" and not "негативный")
            return BadRequest(new { error = "sentiment must be one of: позитивный, нейтральный, негативный" });

        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        var owns = await db.Companies.AnyAsync(
            c => c.Id == job.CompanyId && c.OwnerUserId == ownerId, ct);
        if (!owns) return NotFound();

        var result = await sentimentReviewsService.ListAsync(
            new SentimentReviewsQuery(
                JobId: jobId,
                BranchIds: branchList,
                Sentiment: sentiment,
                From: from,
                To: to,
                Sources: ParseCsv(sources),
                Stars: ParseStars(stars),
                Limit: limit ?? 50,
                Offset: offset ?? 0),
            ct);

        return Ok(new SentimentReviewsDto(
            Items: result.Items
                .Select(i => new SentimentReviewItemDto(i.Id, i.Source, i.ReviewDate, i.Stars, i.Text))
                .ToList(),
            HasMore: result.HasMore));
    }

    // Метрика 4 «Свежий пульс» (per-branch). Окно жёстко 30 дней от server now —
    // period дашборда сюда не передаётся, sentiments тоже (метрика про
    // sentiments). Применяются: branchIds, sources, stars.
    [HttpGet("fresh-pulse")]
    [ProducesResponseType(typeof(FreshPulseMetricDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<FreshPulseMetricDto>> FreshPulse(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] string? sources,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (freshPulseService is null)
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

        var result = await freshPulseService.ComputeAsync(
            new FreshPulseMetricQuery(
                JobId: jobId,
                BranchIds: branchList,
                Sources: ParseCsv(sources),
                Stars: ParseStars(stars)),
            ct);

        return Ok(new FreshPulseMetricDto(
            Current: ToWindowDto(result.Current),
            Previous: ToWindowDto(result.Previous)));

        static FreshPulseWindowDto ToWindowDto(FreshPulseWindowResult w) => new(
            w.Index, w.Positive, w.Neutral, w.Negative,
            w.TotalNonEmpty, w.FromInclusive, w.ToExclusive);
    }

    // Метрика 5 «О чём говорят чаще всего» — топ-3 темы по числу отзывов.
    // Sentiments сюда НЕ принимаем (метрика про разрез pos/neg внутри тем).
    // Применяются: branch, period, sources, stars.
    [HttpGet("top-topics")]
    [ProducesResponseType(typeof(TopTopicsMetricDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TopTopicsMetricDto>> TopTopics(
        Guid jobId,
        [FromQuery] string? branchIds,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sources,
        [FromQuery] string? stars,
        CancellationToken ct)
    {
        if (topTopicsService is null)
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

        var result = await topTopicsService.ComputeAsync(
            new TopTopicsMetricQuery(
                JobId: jobId,
                BranchIds: branchList,
                From: from,
                To: to,
                Sources: ParseCsv(sources),
                Stars: ParseStars(stars)),
            ct);

        return Ok(new TopTopicsMetricDto(
            Topics: result.Topics
                .Select(t => new TopicAggregateDto(t.Topic, t.ReviewCount, t.PositiveMentions, t.NegativeMentions))
                .ToList(),
            TotalReviewsInPeriod: result.TotalReviewsInPeriod));
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
