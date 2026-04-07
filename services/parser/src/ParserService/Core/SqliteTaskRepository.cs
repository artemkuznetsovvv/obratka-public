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
}
