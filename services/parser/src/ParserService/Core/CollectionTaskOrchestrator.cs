using System.Text.Json;
using Microsoft.Extensions.Logging;
using ParserService.Api.Contracts;
using ParserService.Core.Models;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Storage;
using Serilog.Context;

namespace ParserService.Core;

public class CollectionTaskOrchestrator
{
    private readonly ITaskRepository _repository;
    private readonly IS3ResultStorage _s3;
    private readonly IEnumerable<IReviewSourcePlugin> _plugins;
    private readonly IPerSourceRateLimiter _rateLimiter;
    private readonly TaskQueue _taskQueue;
    private readonly ILogger<CollectionTaskOrchestrator> _logger;

    private static readonly JsonSerializerOptions BranchJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// Время на финальное bookkeeping (S3 upload + SQLite update) ПОСЛЕ возможной отмены
    /// внешнего ct. Должно превышать ожидаемое время S3 upload + одного DB write.
    private static readonly TimeSpan BookkeepingTimeout = TimeSpan.FromSeconds(30);

    public CollectionTaskOrchestrator(
        ITaskRepository repository,
        IS3ResultStorage s3,
        IEnumerable<IReviewSourcePlugin> plugins,
        IPerSourceRateLimiter rateLimiter,
        TaskQueue taskQueue,
        ILogger<CollectionTaskOrchestrator> logger)
    {
        _repository = repository;
        _s3 = s3;
        _plugins = plugins;
        _rateLimiter = rateLimiter;
        _taskQueue = taskQueue;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchBranchResult>> SearchAsync(
        CompanySearchRequest request, CancellationToken ct)
    {
        var matchingPlugins = _plugins
            .Where(p => request.Sources.Contains(p.Source))
            .ToList();

        var tasks = matchingPlugins.Select(async plugin =>
        {
            try
            {
                return await plugin.SearchBranchesAsync(request, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search failed for source {Source}", plugin.Source);
                return (IReadOnlyList<SearchBranchResult>)[];
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    public async Task<Guid> StartCollectionAsync(
        CreateCollectionTaskRequest request, CancellationToken ct)
    {
        // Трейс для in-request логов создания задачи (request-summary обогащается из HttpContext.Items
        // в CollectionTasksController — LogContext-scope сюда не дотягивается до строки summary).
        using var _job = LogContext.PushProperty("AnalysisJobId", request.JobId);
        using var _company = LogContext.PushProperty("CompanyId", request.CompanyId);

        var source = SourceTypeExtensions.FromSlug(request.Source);

        var task = new CollectionTask
        {
            Id = Guid.NewGuid(),
            JobId = request.JobId,
            CompanyId = request.CompanyId,
            Source = source,
            Status = CollectionTaskStatus.Pending,
            Progress = 0,
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            BranchesJson = JsonSerializer.Serialize(request.Branches, BranchJsonOptions)
        };

        await _repository.CreateAsync(task, ct);
        await _taskQueue.EnqueueAsync(task.Id, ct);

        _logger.LogInformation(
            "Collection task {TaskId} created for source {Source} (job {AnalysisJobId})",
            task.Id, source, request.JobId);

        return task.Id;
    }

    /// Выполнение задачи сбора. Принципы:
    ///   - Сбор отзывов через плагин использует `ct` (внешняя отмена → стоп).
    ///   - Финальное bookkeeping (запись в S3 + статус-апдейт в SQLite) идёт с независимым
    ///     токеном (timeout, не привязан к внешнему ct), чтобы при graceful-shutdown / SIGTERM
    ///     не терять уже собранные данные.
    ///   - Inter-org delay из `ReleaseOrgSlotAsync` не должен валить весь job — оборачиваем в
    ///     try/catch и логируем как warning.
    public async Task ExecuteCollectionAsync(Guid taskId, CancellationToken ct)
    {
        var task = await _repository.GetByIdAsync(taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("Task {TaskId} not found for execution", taskId);
            return;
        }

        // Восстановление сквозного трейса в фоновом воркере: LogContext (AsyncLocal) НЕ переходит
        // границу in-memory TaskQueue, поэтому берём поля из персистентной CollectionTask. Так ВСЕ
        // строки плагинов (TwoGis/Yandex/Google/BrowserScroll) внутри метода несут AnalysisJobId(=JobId)/
        // CompanyId/Source/TaskId/инициатора без правок самих плагинов. X-Correlation-ID исходного
        // POST сюда не доходит (канал несёт лишь taskId) — устойчивый трейс фона = AnalysisJobId.
        using var _job = LogContext.PushProperty("AnalysisJobId", task.JobId);
        using var _company = LogContext.PushProperty("CompanyId", task.CompanyId);
        using var _src = LogContext.PushProperty("Source", task.Source.ToSlug());
        using var _task = LogContext.PushProperty("TaskId", task.Id);
        using var _initiator = LogContext.PushProperty("Initiator", "system:parser-collection");

        task.Status = CollectionTaskStatus.Running;
        task.Progress = 0;
        await _repository.UpdateAsync(task, ct);

        var allReviews = new List<RawReview>();
        Exception? collectionError = null;

        try
        {
            var plugin = _plugins.FirstOrDefault(p => p.Source == task.Source)
                ?? throw new InvalidOperationException(
                    $"No plugin registered for source {task.Source}");

            var branches = JsonSerializer.Deserialize<List<BranchTargetDto>>(
                task.BranchesJson, BranchJsonOptions) ?? [];

            for (var i = 0; i < branches.Count; i++)
            {
                await _rateLimiter.AcquireOrgSlotAsync(task.Source, ct);
                try
                {
                    var b = branches[i];
                    var target = new BranchTarget(b.BranchId, b.ExternalId, b.ExternalUrl);
                    var period = new DateRange(
                        task.DateFrom ?? DateTimeOffset.MinValue,
                        task.DateTo ?? DateTimeOffset.UtcNow);

                    var reviews = await plugin.FetchReviewsAsync(target, period, ct);
                    allReviews.AddRange(reviews);

                    task.Progress = (double)(i + 1) / branches.Count * 100;
                    task.ReviewCount = allReviews.Count;
                    await _repository.UpdateAsync(task, ct);
                }
                finally
                {
                    // Inter-org delay не должен валить весь job. Если ct отменён или delay
                    // чем-то ломается — это не критично, продолжаем к bookkeeping.
                    try
                    {
                        await _rateLimiter.ReleaseOrgSlotAsync(task.Source, ct);
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogWarning(releaseEx,
                            "ReleaseOrgSlot failed for task {TaskId} (non-fatal, continuing)", taskId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Task {TaskId} collection failed (collected {Count} reviews so far)",
                taskId, allReviews.Count);
            collectionError = ex;
        }

        // Bookkeeping с независимым токеном — не теряем данные при отмене внешнего ct.
        // Если внешний ct отменён, у нас всё ещё есть BookkeepingTimeout на финализацию.
        using var bookkeepingCts = new CancellationTokenSource(BookkeepingTimeout);
        var bk = bookkeepingCts.Token;

        if (collectionError is null && allReviews.Count == 0)
        {
            // Пустой сбор без ошибки: «новых отзывов нет» — это НЕ ошибка (контракт плагина),
            // но и НЕ повод заливать пустой файл в S3: иначе мы затёрли бы прежний
            // raw/{source}.json (важно для инкрементального сбора live-мониторинга — на цикле
            // без новых отзывов нельзя терять ранее собранное сырьё в S3). Просто помечаем
            // completed с review_count=0; S3Url НЕ трогаем (PG поймёт «нет артефакта = 0 новых»).
            try
            {
                task.Status = CollectionTaskStatus.Completed;
                task.Progress = 100;
                task.ReviewCount = 0;
                task.Error = null;
                await _repository.UpdateAsync(task, bk);

                _logger.LogInformation(
                    "Task {TaskId} bookkeeping done: status=completed, 0 reviews (нет новых — S3 не трогаем)",
                    taskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final UpdateAsync failed for task {TaskId}", taskId);
            }
        }
        else if (collectionError is null)
        {
            // Успех с данными — заливаем результат в S3 и помечаем completed.
            try
            {
                var result = new CollectionResult(
                    task.Id,
                    task.JobId,
                    task.Source.ToSlug(),
                    task.CompanyId,
                    DateTimeOffset.UtcNow,
                    allReviews);

                var s3Url = await _s3.UploadResultAsync(result, bk);

                task.Status = CollectionTaskStatus.Completed;
                task.S3Url = s3Url;
                task.Progress = 100;
                task.ReviewCount = allReviews.Count;
                task.Error = null;
                await _repository.UpdateAsync(task, bk);

                _logger.LogInformation(
                    "Task {TaskId} bookkeeping done: status=completed, {ReviewCount} reviews → {S3Url}",
                    taskId, allReviews.Count, s3Url);
            }
            catch (Exception bookkeepingEx)
            {
                _logger.LogError(bookkeepingEx,
                    "Bookkeeping FAILED for task {TaskId} after collecting {Count} reviews — data may be lost",
                    taskId, allReviews.Count);

                // Last-ditch: помечаем таску failed, чтобы она не висела в running.
                try
                {
                    task.Status = CollectionTaskStatus.Failed;
                    task.Error = $"Bookkeeping failed: {bookkeepingEx.Message}";
                    await _repository.UpdateAsync(task, CancellationToken.None);
                }
                catch (Exception finalEx)
                {
                    _logger.LogError(finalEx, "Final UpdateAsync also failed for task {TaskId}", taskId);
                }
            }
        }
        else if (allReviews.Count > 0)
        {
            // Частичный сбор: была ошибка, но что-то успели собрать — данные сохраняем, статус failed.
            try
            {
                var result = new CollectionResult(
                    task.Id,
                    task.JobId,
                    task.Source.ToSlug(),
                    task.CompanyId,
                    DateTimeOffset.UtcNow,
                    allReviews);

                var s3Url = await _s3.UploadResultAsync(result, bk);

                task.Status = CollectionTaskStatus.Failed;
                task.S3Url = s3Url;
                task.Progress = 100;
                task.ReviewCount = allReviews.Count;
                task.Error = collectionError.Message;
                await _repository.UpdateAsync(task, bk);

                _logger.LogWarning(
                    "Task {TaskId} partial: status=failed, но {ReviewCount} отзывов сохранено → {S3Url}",
                    taskId, allReviews.Count, s3Url);
            }
            catch (Exception bookkeepingEx)
            {
                _logger.LogError(bookkeepingEx,
                    "Bookkeeping FAILED for task {TaskId} after collecting {Count} reviews — data may be lost",
                    taskId, allReviews.Count);

                try
                {
                    task.Status = CollectionTaskStatus.Failed;
                    task.Error = $"Bookkeeping failed: {bookkeepingEx.Message}";
                    await _repository.UpdateAsync(task, CancellationToken.None);
                }
                catch (Exception finalEx)
                {
                    _logger.LogError(finalEx, "Final UpdateAsync also failed for task {TaskId}", taskId);
                }
            }
        }
        else
        {
            // Реальная ошибка и ничего не собрано — failure path.
            try
            {
                task.Status = CollectionTaskStatus.Failed;
                task.Error = collectionError.Message;
                await _repository.UpdateAsync(task, bk);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final UpdateAsync failed for task {TaskId}", taskId);
            }
        }
    }
}
