using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ParserService.Core;

/// <summary>
/// Конфиг пула воркеров обработки task'ов сбора.
/// </summary>
public class WorkersOptions
{
    public const string SectionName = "Workers";

    /// <summary>
    /// Сколько task'ов одновременно тянем из очереди и обрабатываем параллельно.
    /// Per-source rate-limiter (`PerSourceRateLimiter.MaxConcurrentOrgs`) защищает от того,
    /// чтобы N воркеров не долбили один и тот же источник параллельно — он сериализует
    /// внутри source'а через семафор. Поэтому 3 воркера = типичный job (yandex/2gis/google)
    /// идёт всеми тремя источниками одновременно вместо последовательно.
    ///
    /// Верхняя граница ограничена BrowserPoolOptions.MaxContexts: каждый task держит
    /// 1-2 browser-контекста (search + collection), при N &gt; MaxContexts воркеры начнут
    /// блокироваться на browser-семафоре.
    /// </summary>
    public int MaxConcurrent { get; set; } = 3;
}

public class CollectionTaskBackgroundService : BackgroundService
{
    private readonly TaskQueue _taskQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkersOptions _options;
    private readonly ILogger<CollectionTaskBackgroundService> _logger;

    public CollectionTaskBackgroundService(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        IOptions<WorkersOptions> options,
        ILogger<CollectionTaskBackgroundService> logger)
    {
        _taskQueue = taskQueue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, _options.MaxConcurrent);
        _logger.LogInformation(
            "Collection task background service started with {WorkerCount} parallel worker(s)",
            workerCount);

        var workers = Enumerable.Range(1, workerCount)
            .Select(id => RunWorkerAsync(id, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var taskId in _taskQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogDebug("[Worker {WorkerId}] picked up task {TaskId}", workerId, taskId);
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<CollectionTaskOrchestrator>();
                await orchestrator.ExecuteCollectionAsync(taskId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Worker {WorkerId}] unhandled error processing task {TaskId}", workerId, taskId);
            }
        }
    }
}
