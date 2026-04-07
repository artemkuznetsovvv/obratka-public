using System.Text.Json;
using Microsoft.Extensions.Logging;
using ParserService.Api.Contracts;
using ParserService.Core.Models;
using ParserService.Infrastructure.Storage;

namespace ParserService.Core;

public class CollectionTaskOrchestrator
{
    private readonly ITaskRepository _repository;
    private readonly IS3ResultStorage _s3;
    private readonly IEnumerable<IReviewSourcePlugin> _plugins;
    private readonly TaskQueue _taskQueue;
    private readonly ILogger<CollectionTaskOrchestrator> _logger;

    private static readonly JsonSerializerOptions BranchJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CollectionTaskOrchestrator(
        ITaskRepository repository,
        IS3ResultStorage s3,
        IEnumerable<IReviewSourcePlugin> plugins,
        TaskQueue taskQueue,
        ILogger<CollectionTaskOrchestrator> logger)
    {
        _repository = repository;
        _s3 = s3;
        _plugins = plugins;
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

        _logger.LogInformation("Collection task {TaskId} created for source {Source}",
            task.Id, source);

        return task.Id;
    }

    public async Task ExecuteCollectionAsync(Guid taskId, CancellationToken ct)
    {
        var task = await _repository.GetByIdAsync(taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("Task {TaskId} not found for execution", taskId);
            return;
        }

        task.Status = CollectionTaskStatus.Running;
        task.Progress = 0;
        await _repository.UpdateAsync(task, ct);

        try
        {
            var plugin = _plugins.FirstOrDefault(p => p.Source == task.Source)
                ?? throw new InvalidOperationException(
                    $"No plugin registered for source {task.Source}");

            var branches = JsonSerializer.Deserialize<List<BranchTargetDto>>(
                task.BranchesJson, BranchJsonOptions) ?? [];

            var allReviews = new List<RawReview>();

            for (var i = 0; i < branches.Count; i++)
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

            var result = new CollectionResult(
                task.Id,
                task.JobId,
                task.Source.ToSlug(),
                task.CompanyId,
                DateTimeOffset.UtcNow,
                allReviews);

            var s3Url = await _s3.UploadResultAsync(result, ct);

            task.Status = CollectionTaskStatus.Completed;
            task.S3Url = s3Url;
            task.Progress = 100;
            task.ReviewCount = allReviews.Count;
            await _repository.UpdateAsync(task, ct);

            _logger.LogInformation(
                "Task {TaskId} completed: {ReviewCount} reviews uploaded to {S3Url}",
                taskId, allReviews.Count, s3Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed", taskId);

            task.Status = CollectionTaskStatus.Failed;
            task.Error = ex.Message;
            await _repository.UpdateAsync(task, ct);
        }
    }
}
