using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ParserService.Core;

public class CollectionTaskBackgroundService : BackgroundService
{
    private readonly TaskQueue _taskQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CollectionTaskBackgroundService> _logger;

    public CollectionTaskBackgroundService(
        TaskQueue taskQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<CollectionTaskBackgroundService> logger)
    {
        _taskQueue = taskQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Collection task background service started");

        await foreach (var taskId in _taskQueue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<CollectionTaskOrchestrator>();
                await orchestrator.ExecuteCollectionAsync(taskId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing task {TaskId}", taskId);
            }
        }
    }
}
