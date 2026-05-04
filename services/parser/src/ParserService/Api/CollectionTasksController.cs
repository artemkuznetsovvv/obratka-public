using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ParserService.Api.Contracts;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;

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

    [HttpGet]
    [ProducesResponseType(typeof(CollectionTaskListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectionTaskListResponse>> List(
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        CollectionTaskStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<CollectionTaskStatus>(status, true, out var parsedStatus))
                return BadRequest(new { error = $"Unknown status: '{status}'. Allowed: pending, running, completed, failed" });
            statusFilter = parsedStatus;
        }

        SourceType? sourceFilter = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!SourceTypeExtensions.TryFromSlug(source, out var parsedSource))
                return BadRequest(new { error = $"Unknown source: '{source}'" });
            sourceFilter = parsedSource;
        }

        var take = Math.Clamp(limit ?? 50, 1, 500);
        var skip = Math.Max(offset ?? 0, 0);

        var tasks = await _repository.ListAsync(statusFilter, sourceFilter, take, skip, ct);

        var items = tasks.Select(t => new CollectionTaskListItem(
            t.Id,
            t.JobId,
            t.CompanyId,
            t.Source.ToSlug(),
            t.Status.ToString().ToLowerInvariant(),
            t.Progress,
            t.ReviewCount,
            t.S3Url,
            t.Error,
            t.CreatedAt,
            t.UpdatedAt)).ToList();

        return Ok(new CollectionTaskListResponse(items.Count, take, skip, items));
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
    /// QA-endpoint: проверка fingerprint браузера через bot.sannysoft.com.
    /// GET /api/collection-tasks/qa/fingerprint?profile=Moderate
    /// </summary>
    [HttpGet("qa/fingerprint")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> QaFingerprint(
        [FromQuery] string? profile,
        [FromQuery] string? source,
        [FromServices] IBrowserPool browserPool,
        [FromServices] Infrastructure.Stealth.IStealthConfigurator stealthConfigurator,
        [FromServices] Infrastructure.Proxy.IProxyRotator proxyRotator,
        CancellationToken ct)
    {
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
                    WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                });

                await Task.Delay(5000, ct);

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
    /// QA-endpoint: разведка API и DOM страницы 2GIS. Только в Development.
    /// GET /api/collection-tasks/qa/2gis-explore?firmId=70000001042201958
    /// Перехватывает все запросы к public-api.reviews.2gis.com и возвращает тела ответов.
    /// </summary>
    [HttpGet("qa/2gis-explore")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Qa2GisExplore(
        [FromQuery] string firmId,
        [FromServices] IBrowserPool browserPool,
        [FromServices] Infrastructure.Stealth.IStealthConfigurator stealthConfigurator,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "QA endpoint is only available in Development" });

        var browserContext = await browserPool.AcquireAsync(new BrowserAcquireOptions(null), ct);
        try
        {
            await stealthConfigurator.ApplyStealthAsync(browserContext,
                Infrastructure.Stealth.StealthProfile.Moderate, ct);

            var page = await browserContext.NewPageAsync();
            var apiResponses = new System.Collections.Concurrent.ConcurrentBag<object>();
            var allXhrUrls = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Intercept responses from reviews API BEFORE navigation
            page.Response += (_, response) =>
            {
                var u = response.Url;
                if (response.Request.ResourceType is "xhr" or "fetch"
                    && (u.Contains("reviews.2gis.com") || u.Contains("catalog.api.2gis")))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var body = await response.TextAsync();
                            apiResponses.Add(new
                            {
                                url = u.Length > 500 ? u[..500] : u,
                                status = response.Status,
                                body_length = body.Length,
                                body_preview = body.Length > 3000 ? body[..3000] : body
                            });
                        }
                        catch { }
                    });
                }

                if (response.Request.ResourceType is "xhr" or "fetch")
                    allXhrUrls.Add(u.Length > 300 ? u[..300] : u);
            };

            try
            {
                // Navigate directly to reviews tab
                var url = $"https://2gis.ru/moscow/firm/{firmId}/tab/reviews";
                await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle,
                    Timeout = 30_000
                });
                await Task.Delay(3000, ct);

                // Try scrolling to trigger more reviews loading
                await page.EvaluateAsync("""
                    async () => {
                        const c = document.querySelector('[data-scroll="true"]')
                            || document.querySelector('.scroll__container')
                            || document.scrollingElement;
                        if (c) {
                            c.scrollTop = c.scrollHeight;
                            await new Promise(r => setTimeout(r, 2000));
                        }
                    }
                """);
                await Task.Delay(3000, ct);

                return Ok(new
                {
                    firm_id = firmId,
                    api_responses_count = apiResponses.Count,
                    api_responses = apiResponses,
                    all_xhr_urls = allXhrUrls.Where(u =>
                        !u.Contains("tile") && !u.Contains("mapgl") &&
                        !u.Contains("d-assets") && !u.Contains("disk.2gis") &&
                        !u.Contains("yandex") && !u.Contains("rambler") &&
                        !u.Contains("sberbank")).ToList()
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
        }
    }

    /// <summary>
    /// QA-endpoint: проверка здоровья прокси. Только в Development.
    /// GET /api/collection-tasks/qa/proxy-health?timeoutMs=15000&amp;enabledOnly=true&amp;id=3
    /// Источник прокси — таблица Proxies в SQLite. Для каждого прокси делает запросы:
    ///   - api.ipify.org — живость + exit-IP
    ///   - google.com/maps, yandex.ru, 2gis.ru — геоблок/блок конкретным сайтом
    /// Возвращает статус, финальный URL после редиректов, латентность и ошибку по каждой цели,
    /// а также текущее состояние прокси из БД (id, enabled, failure_count, cooldown_until).
    /// </summary>
    [HttpGet("qa/proxy-health")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> QaProxyHealth(
        [FromQuery] int? timeoutMs,
        [FromQuery] bool? enabledOnly,
        [FromQuery] int? id,
        [FromServices] IProxyRepository proxyRepository,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "QA endpoint is only available in Development" });

        var timeout = TimeSpan.FromMilliseconds(timeoutMs is > 0 ? timeoutMs.Value : 15_000);

        IReadOnlyList<Core.Models.ProxyEntity> proxies;
        if (id.HasValue)
        {
            var single = await proxyRepository.GetByIdAsync(id.Value, ct);
            if (single is null) return NotFound(new { error = $"Proxy id={id} not found" });
            proxies = [single];
        }
        else
        {
            proxies = await proxyRepository.ListAsync(enabledOnly, ct);
        }

        if (proxies.Count == 0)
            return Ok(new { configured = 0, results = Array.Empty<object>() });

        var targets = new (string name, string url)[]
        {
            ("ipify",       "https://api.ipify.org?format=json"),
            ("google_maps", "https://www.google.com/maps"),
            ("yandex",      "https://yandex.ru/maps/"),
            ("2gis",        "https://2gis.ru/"),
        };

        var results = new List<object>();
        foreach (var entry in proxies)
        {
            string protocol;
            try
            {
                protocol = NormalizeEntryProtocol(entry.Protocol);
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    id = entry.Id,
                    address = $"{entry.Host}:{entry.Port}",
                    protocol = entry.Protocol,
                    enabled = entry.Enabled,
                    ok = false,
                    error = ex.Message
                });
                continue;
            }

            var proxyUrl = $"{protocol}://{entry.Host}:{entry.Port}";
            var webProxy = new WebProxy(new Uri(proxyUrl));
            if (!string.IsNullOrEmpty(entry.Username))
                webProxy.Credentials = new NetworkCredential(entry.Username, entry.Password);

            using var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                ConnectTimeout = timeout,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");

            var targetResults = new List<object>();
            foreach (var (name, url) in targets)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var response = await client.GetAsync(url, ct);
                    sw.Stop();
                    var body = await response.Content.ReadAsStringAsync(ct);
                    targetResults.Add(new
                    {
                        target = name,
                        ok = response.IsSuccessStatusCode,
                        status = (int)response.StatusCode,
                        elapsed_ms = sw.ElapsedMilliseconds,
                        final_url = response.RequestMessage?.RequestUri?.ToString(),
                        body_length = body.Length,
                        body_preview = body.Length > 200 ? body[..200] : body
                    });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    targetResults.Add(new
                    {
                        target = name,
                        ok = false,
                        elapsed_ms = sw.ElapsedMilliseconds,
                        error = ex.GetType().Name + ": " + ex.Message,
                        inner = ex.InnerException?.Message
                    });
                }
            }

            var proxyOk = targetResults.Any(r => (bool)r.GetType().GetProperty("ok")!.GetValue(r)!);
            results.Add(new
            {
                id = entry.Id,
                address = $"{entry.Host}:{entry.Port}",
                protocol,
                enabled = entry.Enabled,
                failure_count = entry.FailureCount,
                cooldown_until = entry.CooldownUntil,
                last_used_at = entry.LastUsedAt,
                notes = entry.Notes,
                ok = proxyOk,
                targets = targetResults
            });
        }

        return Ok(new
        {
            configured = proxies.Count,
            healthy = results.Count(r => (bool)r.GetType().GetProperty("ok")!.GetValue(r)!),
            results
        });
    }

    /// <summary>
    /// QA-endpoint: прогоняет Playwright через текущий прокси и логирует что рушится.
    /// GET /api/collection-tasks/qa/proxy-browser?url=https://www.google.com/maps&timeoutMs=30000
    /// Перехватывает console, pageerror, requestfailed — покажет cert/tunnel/blocked ошибки Chromium.
    /// </summary>
    [HttpGet("qa/proxy-browser")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> QaProxyBrowser(
        [FromQuery] string? url,
        [FromQuery] int? timeoutMs,
        [FromServices] IBrowserPool browserPool,
        [FromServices] Infrastructure.Stealth.IStealthConfigurator stealthConfigurator,
        [FromServices] IProxyRotator proxyRotator,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "QA endpoint is only available in Development" });

        var target = string.IsNullOrWhiteSpace(url) ? "https://www.google.com/maps" : url;
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : 30_000;

        // Жёсткий общий бюджет — чтобы ручка ни при каких условиях не висела дольше timeout + 15s
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hardCts.CancelAfter(timeout + 15_000);
        var hardCt = hardCts.Token;

        var proxy = await proxyRotator.GetProxyAsync(SourceType.GoogleMaps, hardCt);
        var context = await browserPool.AcquireAsync(new BrowserAcquireOptions(proxy), hardCt);
        var consoleMsgs = new System.Collections.Concurrent.ConcurrentBag<object>();
        var pageErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var requestFailures = new System.Collections.Concurrent.ConcurrentBag<object>();
        var responses = new System.Collections.Concurrent.ConcurrentBag<object>();

        try
        {
            await stealthConfigurator.ApplyStealthAsync(context,
                Infrastructure.Stealth.StealthProfile.Moderate, hardCt);

            var page = await context.NewPageAsync();

            page.Console += (_, msg) =>
            {
                if (msg.Type is "error" or "warning")
                    consoleMsgs.Add(new { type = msg.Type, text = msg.Text });
            };
            page.PageError += (_, err) => pageErrors.Add(err);
            page.RequestFailed += (_, req) =>
            {
                var u = req.Url;
                requestFailures.Add(new
                {
                    url = u.Length > 200 ? u[..200] : u,
                    failure = req.Failure,
                    method = req.Method,
                    resource_type = req.ResourceType
                });
            };
            page.Response += (_, resp) =>
            {
                var u = resp.Url;
                if (resp.Status >= 400 || u.Contains("consent.") || u.Contains("showcaptcha") || u.Contains("/sorry/"))
                    responses.Add(new
                    {
                        url = u.Length > 200 ? u[..200] : u,
                        status = resp.Status,
                        resource_type = resp.Request.ResourceType
                    });
            };

            string? navigationError = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await page.GotoAsync(target, new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                    Timeout = timeout
                });
            }
            catch (Exception ex)
            {
                navigationError = ex.GetType().Name + ": " + ex.Message;
            }
            sw.Stop();

            try { await Task.Delay(1500, hardCt); } catch { }

            var finalUrl = page.Url;
            string? title = null;
            try { title = await page.TitleAsync(); } catch { }

            return Ok(new
            {
                proxy = proxy != null ? $"{proxy.Protocol}://{proxy.Host}:{proxy.Port}" : "none",
                target,
                elapsed_ms = sw.ElapsedMilliseconds,
                navigation_error = navigationError,
                final_url = finalUrl,
                title,
                console_count = consoleMsgs.Count,
                console = consoleMsgs.Take(20),
                page_errors = pageErrors,
                request_failures_count = requestFailures.Count,
                request_failures = requestFailures.Take(30),
                notable_responses = responses.Take(30)
            });
        }
        finally
        {
            await browserPool.ReleaseAsync(context);
            if (proxy != null) await proxyRotator.ReleaseProxyAsync(proxy);
        }
    }

    /// <summary>
    /// QA-endpoint: low-level TCP CONNECT через прокси (без Chromium, без HttpClient-магии).
    /// GET /api/collection-tasks/qa/proxy-connect?host=www.google.com&port=443&timeoutMs=8000
    /// Делает сырой HTTP CONNECT и возвращает строку-ответ прокси. Если не 200 — прокси режет
    /// именно этот домен, и никакая настройка Chromium не поможет.
    /// </summary>
    [HttpGet("qa/proxy-connect")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> QaProxyConnect(
        [FromQuery] string? host,
        [FromQuery] int? port,
        [FromQuery] int? timeoutMs,
        [FromQuery] int? id,
        [FromServices] IProxyRepository proxyRepository,
        CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "QA endpoint is only available in Development" });

        Core.Models.ProxyEntity? entry;
        if (id.HasValue)
        {
            entry = await proxyRepository.GetByIdAsync(id.Value, ct);
            if (entry is null)
                return NotFound(new { error = $"Proxy id={id} not found" });
        }
        else
        {
            var rows = await proxyRepository.ListAsync(enabledOnly: true, ct);
            entry = rows.FirstOrDefault();
            if (entry is null)
                return BadRequest(new { error = "No enabled proxies in DB" });
        }

        var targetHost = string.IsNullOrWhiteSpace(host) ? "www.google.com" : host;
        var targetPort = port is > 0 ? port.Value : 443;
        var timeout = TimeSpan.FromMilliseconds(timeoutMs is > 0 ? timeoutMs.Value : 8_000);

        string protocol;
        try { protocol = NormalizeEntryProtocol(entry.Protocol); }
        catch (Exception ex) { return Ok(new { error = ex.Message }); }

        if (protocol != "http" && protocol != "https")
            return Ok(new { error = $"TCP CONNECT probe supports only http/https proxies; got {protocol}" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeout);

            await tcp.ConnectAsync(entry.Host, entry.Port, connectCts.Token);
            var tcpConnectedMs = sw.ElapsedMilliseconds;

            System.IO.Stream stream = tcp.GetStream();
            if (protocol == "https")
            {
                var ssl = new System.Net.Security.SslStream(stream, false);
                await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                {
                    TargetHost = entry.Host
                }, connectCts.Token);
                stream = ssl;
            }

            var authHeader = !string.IsNullOrEmpty(entry.Username)
                ? "Proxy-Authorization: Basic " + Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes($"{entry.Username}:{entry.Password}")) + "\r\n"
                : "";

            var request =
                $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\n" +
                $"Host: {targetHost}:{targetPort}\r\n" +
                authHeader +
                "Proxy-Connection: keep-alive\r\n" +
                "User-Agent: Mozilla/5.0\r\n" +
                "\r\n";

            var reqBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(reqBytes, connectCts.Token);
            await stream.FlushAsync(connectCts.Token);

            var buf = new byte[4096];
            var read = await stream.ReadAsync(buf, connectCts.Token);
            sw.Stop();

            var response = System.Text.Encoding.ASCII.GetString(buf, 0, read);
            var firstLine = response.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var headers = response.Split("\r\n\r\n", 2)[0];

            return Ok(new
            {
                proxy_id = entry.Id,
                proxy_notes = entry.Notes,
                proxy = $"{protocol}://{entry.Host}:{entry.Port}",
                target = $"{targetHost}:{targetPort}",
                tcp_connected_ms = tcpConnectedMs,
                total_ms = sw.ElapsedMilliseconds,
                status_line = firstLine,
                ok = firstLine.Contains("200"),
                headers
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(new
            {
                proxy_id = entry.Id,
                proxy_notes = entry.Notes,
                proxy = $"{protocol}://{entry.Host}:{entry.Port}",
                target = $"{targetHost}:{targetPort}",
                total_ms = sw.ElapsedMilliseconds,
                ok = false,
                error = ex.GetType().Name + ": " + ex.Message,
                inner = ex.InnerException?.Message
            });
        }
    }

    private static string NormalizeEntryProtocol(string? protocol)
    {
        var p = (protocol ?? "http").Trim().ToLowerInvariant();
        return p switch
        {
            "http" or "https" => p,
            _ => throw new InvalidOperationException(
                $"Unsupported proxy protocol '{protocol}'. Allowed: http, https.")
        };
    }


    /// <summary>
    /// QA-endpoint: прямой вызов плагина по businessId.
    /// GET /api/collection-tasks/qa/yandex/{businessId}?date_from=2024-01-01
    /// </summary>
    [HttpGet("qa/{source}/{externalId}")]
    [RequireQaApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> QaFetchReviews(
        string source, string externalId,
        [FromQuery] DateTimeOffset? date_from,
        [FromQuery] string? url,
        CancellationToken ct)
    {
        if (!SourceTypeExtensions.TryFromSlug(source, out var sourceType))
            return BadRequest(new { error = $"Unknown source: '{source}'" });

        var plugin = _plugins.FirstOrDefault(p => p.Source == sourceType);
        if (plugin is null)
            return NotFound(new { error = $"Plugin not found for source: '{source}'" });

        var branch = new BranchTarget(
            BranchId: Guid.NewGuid(),
            ExternalId: externalId,
            ExternalUrl: url ?? "");

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
