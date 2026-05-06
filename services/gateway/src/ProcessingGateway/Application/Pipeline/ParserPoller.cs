using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Ingestion;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Parser;
using ProcessingGateway.Infrastructure.Parser.Contracts;
using ProcessingGateway.Infrastructure.Storage;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Pipeline;

/// Фоновый поллер задач Parser-а. Каждые `Parser:PollIntervalSeconds` (3-5 сек по ADR-001 §5)
/// сканирует jobs в `status='collecting'`, опрашивает Parser по каждой активной таске,
/// апдейтит `collection_progress[source]`, при `completed` — скачивает s3-файл и ингестит
/// отзывы через `RawReviewBulkInserter` + `JobReviewLinker`. Когда все источники job-а
/// перешли в completed/failed — вызывает `AnalysisOrchestrator.AdvanceAfterCollectionAsync`.
///
/// Идемпотентен: переживает рестарт PG (jobs читаются из БД), повторный ингест того же файла
/// поглощается ON CONFLICT DO NOTHING на reviews и analysis_job_reviews.
///
/// Таймаут per-task: `Parser:TaskTimeoutMinutes` от `analysis_jobs.created_at`. Истёкший
/// task помечается failed с reason=timeout.
public sealed class ParserPoller : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _taskTimeout;
    private readonly ILogger<ParserPoller> _logger;

    public ParserPoller(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<ParserPoller> logger)
    {
        _services = services;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(
            int.TryParse(configuration["Parser:PollIntervalSeconds"], out var p) ? p : 4);
        _taskTimeout = TimeSpan.FromMinutes(
            int.TryParse(configuration["Parser:TaskTimeoutMinutes"], out var t) ? t : 90);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "ParserPoller started: interval={Interval}, taskTimeout={Timeout}",
            _pollInterval, _taskTimeout);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "ParserPoller cycle failed; will retry on next interval");
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcessingDbContext>();

        var activeJobs = await db.AnalysisJobs
            .Where(j => j.Status == AnalysisJobStatus.Collecting)
            .ToListAsync(ct);

        if (activeJobs.Count == 0) return;

        foreach (var job in activeJobs)
        {
            try
            {
                await ProcessJobAsync(scope.ServiceProvider, job, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to process job {AnalysisJobId} in poll cycle; retrying next iteration",
                    job.Id);
            }
        }
    }

    private async Task ProcessJobAsync(IServiceProvider scoped, AnalysisJob job, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("AnalysisJobId", job.Id);

        var db = scoped.GetRequiredService<ProcessingDbContext>();
        var parser = scoped.GetRequiredService<IParserClient>();
        var blob = scoped.GetRequiredService<IJobBlobStorage>();
        var inserter = scoped.GetRequiredService<RawReviewBulkInserter>();
        var linker = scoped.GetRequiredService<JobReviewLinker>();
        var orchestrator = scoped.GetRequiredService<AnalysisOrchestrator>();

        // Копия словаря — мы будем мутировать значения и при наличии изменений сохранять обратно.
        var progress = new Dictionary<string, CollectionProgressEntry>(job.CollectionProgress);

        foreach (var (source, entry) in progress.ToList())
        {
            // Завершённые источники пропускаем.
            if (entry.Status is "completed" or "failed") continue;
            if (entry.TaskId is null)
            {
                // Источник упал ещё на старте — должен быть status=failed уже.
                continue;
            }

            // Таймаут (от created_at). Parser-таска зависла — фиксируем failure и идём дальше.
            var elapsed = DateTimeOffset.UtcNow - job.CreatedAt;
            if (elapsed > _taskTimeout)
            {
                progress[source] = entry with
                {
                    Status = "failed",
                    Error = $"Parser task exceeded timeout {_taskTimeout}"
                };
                _logger.LogWarning(
                    "Parser task {TaskId} for source={Source} timed out after {Elapsed}",
                    entry.TaskId, source, elapsed);
                continue;
            }

            CollectionTaskStatusResponse status;
            try
            {
                status = await parser.GetStatusAsync(entry.TaskId.Value, ct);
            }
            catch (ParserTaskNotFoundException)
            {
                progress[source] = entry with
                {
                    Status = "failed",
                    Error = "Parser task not found (lost or expired)"
                };
                continue;
            }

            switch (status.Status)
            {
                case "pending":
                case "running":
                    // Parser отдаёт progress уже в процентах 0..100 (фактический контракт,
                    // несмотря на ADR-001 §5 где было заявлено 0..1). Без re-scale.
                    progress[source] = entry with
                    {
                        Status = status.Status,
                        Progress = (int)Math.Round(status.Progress)
                    };
                    break;

                case "completed":
                {
                    var s3Url = status.S3Url
                        ?? throw new InvalidOperationException(
                            $"Parser reported completed but no s3_url for task {entry.TaskId}");

                    var payload = await blob.ReadRawAsync(s3Url, ct);
                    var entities = payload.Reviews
                        .Select(r => RawReviewMapper.ToEntity(payload, r))
                        .ToList();

                    await inserter.InsertAsync(entities, ct);
                    await linker.LinkAsync(
                        job.Id,
                        entities.Select(e => e.CompositeKey).ToList(),
                        ct);

                    progress[source] = entry with
                    {
                        Status = "completed",
                        Progress = 100,
                        ReviewCount = payload.Reviews.Count,
                        S3Url = s3Url
                    };

                    _logger.LogInformation(
                        "Source {Source} completed: ingested {ReviewCount} reviews from {S3Url}",
                        source, payload.Reviews.Count, s3Url);
                    break;
                }

                case "failed":
                    progress[source] = entry with
                    {
                        Status = "failed",
                        Error = status.Error ?? "Parser reported failed without error message"
                    };
                    _logger.LogWarning(
                        "Source {Source} failed: {Error}",
                        source, progress[source].Error);
                    break;

                default:
                    _logger.LogWarning(
                        "Unknown Parser status '{Status}' for source={Source}; treating as running",
                        status.Status, source);
                    break;
            }
        }

        // Сохраняем обновлённый progress, если что-то изменилось.
        if (!ProgressEqual(job.CollectionProgress, progress))
        {
            job.CollectionProgress = progress;
            await db.SaveChangesAsync(ct);
        }

        // Проверяем — все ли источники завершились?
        var allFinal = progress.Values.All(e => e.Status is "completed" or "failed");
        if (allFinal)
        {
            await orchestrator.AdvanceAfterCollectionAsync(job.Id, ct);
        }
    }

    private static bool ProgressEqual(
        Dictionary<string, CollectionProgressEntry> a,
        Dictionary<string, CollectionProgressEntry> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var other) || !Equals(v, other)) return false;
        }
        return true;
    }
}
