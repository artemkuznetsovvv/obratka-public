using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Infrastructure.Database;
using Testcontainers.PostgreSql;

namespace ProcessingGateway.Tests.Infrastructure;

/// Один Postgres-контейнер на всю тестовую сборку. Миграция применяется один раз в
/// `InitializeAsync` — все тесты получают уже мигрированную БД.
/// Тесты сами чистят данные между прогонами.
public class PgFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("processing")
        .WithUsername("processing_user")
        .WithPassword("processing_pwd")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        await using var ctx = NewDbContext();
        await ctx.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public ProcessingDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<ProcessingDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .EnableSensitiveDataLogging()
            .Options;
        return new ProcessingDbContext(options);
    }
}

[CollectionDefinition("Postgres")]
public class PgCollection : ICollectionFixture<PgFixture> { }
