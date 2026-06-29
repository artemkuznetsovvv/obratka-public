using ParserService.Core.Models;

namespace ParserService.Core;

public interface ITaskRepository
{
    Task<CollectionTask> CreateAsync(CollectionTask task, CancellationToken ct);
    Task<CollectionTask?> GetByIdAsync(Guid taskId, CancellationToken ct);
    Task UpdateAsync(CollectionTask task, CancellationToken ct);
    Task<IReadOnlyList<CollectionTask>> ListAsync(
        CollectionTaskStatus? status,
        SourceType? source,
        int limit,
        int offset,
        CancellationToken ct);
}
