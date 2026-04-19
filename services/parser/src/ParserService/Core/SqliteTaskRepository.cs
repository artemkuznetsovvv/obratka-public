using Microsoft.EntityFrameworkCore;
using ParserService.Core.Models;

namespace ParserService.Core;

public class SqliteTaskRepository : ITaskRepository
{
    private readonly ParserDbContext _db;

    public SqliteTaskRepository(ParserDbContext db)
    {
        _db = db;
    }

    public async Task<CollectionTask> CreateAsync(CollectionTask task, CancellationToken ct)
    {
        task.CreatedAt = DateTimeOffset.UtcNow;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        _db.CollectionTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<CollectionTask?> GetByIdAsync(Guid taskId, CancellationToken ct)
    {
        return await _db.CollectionTasks.FindAsync([taskId], ct);
    }

    public async Task UpdateAsync(CollectionTask task, CancellationToken ct)
    {
        task.UpdatedAt = DateTimeOffset.UtcNow;
        _db.CollectionTasks.Update(task);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CollectionTask>> ListAsync(
        CollectionTaskStatus? status,
        SourceType? source,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var query = _db.CollectionTasks.AsNoTracking();
        if (status.HasValue) query = query.Where(t => t.Status == status.Value);
        if (source.HasValue) query = query.Where(t => t.Source == source.Value);
        return await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }
}
