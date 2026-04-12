using Microsoft.AspNetCore.Mvc;
using ParserService.Api.Contracts;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;

namespace ParserService.Api;

[ApiController]
[Route("api/collection-tasks")]
public class CollectionTasksController : ControllerBase
{
    private readonly CollectionTaskOrchestrator _orchestrator;
    private readonly ITaskRepository _repository;
    private readonly IEnumerable<IReviewSourcePlugin> _plugins;
    private readonly IWebHostEnvironment _env;

    public CollectionTasksController(
        CollectionTaskOrchestrator orchestrator,
        ITaskRepository repository,
        IEnumerable<IReviewSourcePlugin> plugins,
        IWebHostEnvironment env)
    {
        _orchestrator = orchestrator;
        _repository = repository;
        _plugins = plugins;
        _env = env;
    }

    [HttpPost("search")]
    [ProducesResponseType(typeof(SearchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromBody] SearchRequest request, CancellationToken ct)
    {
        var sources = request.Sources
            .Where(s => SourceTypeExtensions.TryFromSlug(s, out _))
            .Select(s => SourceTypeExtensions.FromSlug(s))
            .ToArray();

        var coreRequest = new CompanySearchRequest(request.Query, request.City, sources);
        var results = await _orchestrator.SearchAsync(coreRequest, ct);

        var dtos = results.Select(r => new SearchBranchResultDto(
            r.Source.ToSlug(), r.ExternalId, r.ExternalUrl,
            r.Name, r.Address, r.Rating, r.ReviewCount
        )).ToList();

        return Ok(new SearchResponse(dtos));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateCollectionTaskResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateCollectionTaskResponse>> Create(
        [FromBody] CreateCollectionTaskRequest request, CancellationToken ct)
    {
        if (!SourceTypeExtensions.TryFromSlug(request.Source, out _))
            return BadRequest(new { error = $"Unknown source: '{request.Source}'" });

        if (request.Branches is not { Count: > 0 })
            return BadRequest(new { error = "At least one branch is required" });

        var taskId = await _orchestrator.StartCollectionAsync(request, ct);
        return AcceptedAtAction(nameof(GetStatus), new { taskId }, new CreateCollectionTaskResponse(taskId));
    }

    [HttpGet("{taskId:guid}")]
    [ProducesResponseType(typeof(CollectionTaskStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CollectionTaskStatusResponse>> GetStatus(
        Guid taskId, CancellationToken ct)
    {
        var task = await _repository.GetByIdAsync(taskId, ct);
        if (task is null)
            return NotFound();

        var response = new CollectionTaskStatusResponse(
            task.Id,
            task.Status.ToString().ToLowerInvariant(),
            task.Source.ToSlug(),
            task.Progress,
            task.ReviewCount,
            task.S3Url,
            task.Error);

        return Ok(response);
    }

    /// <summary>
    /// QA-endpoint: проверка fingerprint браузера через bot.sannysoft.com. Только в Development.
    /// GET /api/collection-tasks/qa/fingerprint?profile=Moderate
    /// </summary>
    [HttpGet("qa/fingerprint")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> QaFingerprint(
        [FromQuery] string? profile,
        [FromQuery] string? source,
        [FromServices] IBrowserPool browserPool,
        [FromServices] Infrastructure.Stealth.IStealthConfigurator stealthConfigurator,
        [FromServices] Infrastructure.Proxy.IProxyRotator proxyRotator,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "QA endpoint is only available in Development" });

        var stealthProfile = Enum.TryParse<Infrastructure.Stealth.StealthProfile>(profile, true, out var p)
            ? p
            : Infrastructure.Stealth.StealthProfile.Moderate;

        var sourceType = Core.Models.SourceType.YandexMaps;
        if (source != null && SourceTypeExtensions.TryFromSlug(source, out var parsed))
            sourceType = parsed;

        var proxy = await proxyRotator.GetProxyAsync(sourceType, ct);
        var browserContext = await browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), ct);

        try
        {
            await stealthConfigurator.ApplyStealthAsync(browserContext, stealthProfile, ct);

            var page = await browserContext.NewPageAsync();
            try
            {
                await page.GotoAsync("https://bot.sannysoft.com/", new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle,
                    Timeout = 30_000
                });

                await Task.Delay(2000, ct);

                var results = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
                    () => {
                        const tests = {};
                        const failed = [];

                        // Main tests table
                        const table = document.querySelector('table');
                        if (table) {
                            table.querySelectorAll('tr').forEach(row => {
                                const cells = row.querySelectorAll('td');
                                if (cells.length >= 2) {
                                    const name = cells[0]?.innerText?.trim();
                                    const value = cells[1]?.innerText?.trim();
                                    const cls = cells[1]?.className || '';
                                    if (name) {
                                        const passed = cls.includes('passed');
                                        tests[name] = { value, passed };
                                        if (!passed) failed.push(name);
                                    }
                                }
                            });
                        }

                        // Fingerprint Scanner table
                        const tables = document.querySelectorAll('table');
                        if (tables.length > 1) {
                            tables[1].querySelectorAll('tr').forEach(row => {
                                const cells = row.querySelectorAll('td');
                                if (cells.length >= 2) {
                                    const name = cells[0]?.innerText?.trim();
                                    const value = cells[1]?.innerText?.trim();
                                    const cls = cells[1]?.className || '';
                                    if (name) {
                                        const passed = cls.includes('passed') || value === 'ok';
                                        tests['FP:' + name] = { value, passed };
                                        if (!passed) failed.push('FP:' + name);
                                    }
                                }
                            });
                        }

                        return {
                            total: Object.keys(tests).length,
                            passed: Object.values(tests).filter(t => t.passed).length,
                            failed_count: failed.length,
                            failed_tests: failed,
                            user_agent: navigator.userAgent,
                            webdriver: navigator.webdriver,
                            languages: navigator.languages,
                            platform: navigator.platform,
                            hardware_concurrency: navigator.hardwareConcurrency,
                            device_memory: navigator.deviceMemory,
                            screen: { width: screen.width, height: screen.height, dpr: window.devicePixelRatio },
                            tests
                        };
                    }
                """);

                return Ok(new
                {
                    stealth_profile = stealthProfile.ToString(),
                    proxy = proxy != null ? $"{proxy.Host}:{proxy.Port}" : "none",
                    diagnostics = results
                });
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            await browserPool.ReleaseAsync(browserContext);
            if (proxy != null) await proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    /// <summary>
    /// QA-endpoint: прямой вызов плагина по businessId. Только в Development.
    /// GET /api/collection-tasks/qa/yandex/{businessId}?date_from=2024-01-01
    /// </summary>
    [HttpGet("qa/{source}/{externalId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> QaFetchReviews(
        string source, string externalId, [FromQuery] DateTimeOffset? date_from, CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "QA endpoint is only available in Development" });

        if (!SourceTypeExtensions.TryFromSlug(source, out var sourceType))
            return BadRequest(new { error = $"Unknown source: '{source}'" });

        var plugin = _plugins.FirstOrDefault(p => p.Source == sourceType);
        if (plugin is null)
            return NotFound(new { error = $"Plugin not found for source: '{source}'" });

        var branch = new BranchTarget(
            BranchId: Guid.NewGuid(),
            ExternalId: externalId,
            ExternalUrl: "");

        var period = new DateRange(
            From: date_from ?? DateTimeOffset.MinValue,
            To: DateTimeOffset.UtcNow);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reviews = await plugin.FetchReviewsAsync(branch, period, ct);
        sw.Stop();

        return Ok(new
        {
            source,
            external_id = externalId,
            review_count = reviews.Count,
            elapsed_ms = sw.ElapsedMilliseconds,
            date_range = new { from = period.From, to = period.To },
            reviews
        });
    }
}
