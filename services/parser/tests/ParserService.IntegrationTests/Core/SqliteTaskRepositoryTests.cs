using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.IntegrationTests.Core;

public class SqliteTaskRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ParserDbContext _db;
    private readonly SqliteTaskRepository _repository;

    public SqliteTaskRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ParserDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new ParserDbContext(options);
        _db.Database.EnsureCreated();
        _repository = new SqliteTaskRepository(_db);
    }

    [Fact]
    public async Task CreateAsync_PersistsTaskAndReturnsIt()
    {
        var task = new CollectionTask
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Source = SourceType.TwoGis,
            Status = CollectionTaskStatus.Pending
        };

        var result = await _repository.CreateAsync(task, CancellationToken.None);

        result.Id.Should().Be(task.Id);
        result.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsTask()
    {
        var task = new CollectionTask
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Source = SourceType.YandexMaps,
            Status = CollectionTaskStatus.Pending
        };

        await _repository.CreateAsync(task, CancellationToken.None);

        var result = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(task.Id);
        result.Source.Should().Be(SourceType.YandexMaps);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ModifiesStatusAndReflectsOnRead()
    {
        var task = new CollectionTask
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Source = SourceType.GoogleMaps,
            Status = CollectionTaskStatus.Pending
        };

        await _repository.CreateAsync(task, CancellationToken.None);

        task.Status = CollectionTaskStatus.Running;
        task.Progress = 50;
        await _repository.UpdateAsync(task, CancellationToken.None);

        var result = await _repository.GetByIdAsync(task.Id, CancellationToken.None);

        result!.Status.Should().Be(CollectionTaskStatus.Running);
        result.Progress.Should().Be(50);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
