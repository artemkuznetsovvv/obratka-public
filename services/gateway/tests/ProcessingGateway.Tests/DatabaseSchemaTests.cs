using Dapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Postgres")]
public class DatabaseSchemaTests
{
    private readonly PgFixture _pg;

    public DatabaseSchemaTests(PgFixture pg) => _pg = pg;

    [Fact]
    public async Task Migration_creates_all_business_tables()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(@"
            SELECT tablename FROM pg_tables WHERE schemaname = 'public'")).ToHashSet();

        tables.Should().Contain(new[]
        {
            "reviews", "review_llm_results", "analysis_jobs",
            "analysis_job_reviews", "analysis_recommendations"
        });
        tables.Should().Contain(new[] { "inbox_state", "outbox_state", "outbox_message" });
    }

    [Fact]
    public async Task Gin_index_on_aspects_exists()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();

        var indexDef = await conn.QuerySingleOrDefaultAsync<string>(@"
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'review_llm_results'
              AND indexname = 'ix_review_llm_results_aspects_gin'");

        indexDef.Should().NotBeNull();
        indexDef.Should().Contain("USING gin");
        indexDef.Should().Contain("(aspects)");
    }

    [Fact]
    public async Task Composite_key_unique_index_blocks_duplicate_inserts()
    {
        var ctx = _pg.NewDbContext();
        await ResetData(ctx);

        var review = new Review
        {
            CompanyId = Guid.NewGuid(), BranchId = Guid.NewGuid(),
            Source = "yandex", CompositeKey = "yandex:b1:ext:abc",
            RawText = "Тест", ReviewDate = DateTimeOffset.UtcNow
        };
        ctx.Reviews.Add(review);
        await ctx.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();

        var inserted = await conn.ExecuteAsync(@"
            INSERT INTO reviews (company_id, branch_id, source, composite_key, raw_text, review_date, collected_at)
            VALUES (@CompanyId, @BranchId, 'yandex', 'yandex:b1:ext:abc', 'Дубль', NOW(), NOW())
            ON CONFLICT (composite_key) DO NOTHING;",
            new { review.CompanyId, review.BranchId });

        inserted.Should().Be(0, "ON CONFLICT (composite_key) должен поглотить дубль");

        var rowCount = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM reviews WHERE composite_key = 'yandex:b1:ext:abc'");
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task Source_branch_external_partial_unique_blocks_duplicates_when_external_id_set()
    {
        var ctx = _pg.NewDbContext();
        await ResetData(ctx);

        var branch = Guid.NewGuid();
        ctx.Reviews.Add(new Review
        {
            CompanyId = Guid.NewGuid(), BranchId = branch,
            Source = "2gis", ExternalId = "ext-42",
            CompositeKey = "2gis:b:ext:ext-42",
            RawText = "x", ReviewDate = DateTimeOffset.UtcNow
        });
        await ctx.SaveChangesAsync();

        ctx.Reviews.Add(new Review
        {
            CompanyId = Guid.NewGuid(), BranchId = branch,
            Source = "2gis", ExternalId = "ext-42",
            CompositeKey = "2gis:b:ext:ext-42:variant",
            RawText = "y", ReviewDate = DateTimeOffset.UtcNow
        });

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task Two_reviews_with_null_external_id_are_allowed()
    {
        // Partial UNIQUE: WHERE external_id IS NOT NULL → null-ам можно повторяться.
        var ctx = _pg.NewDbContext();
        await ResetData(ctx);

        var branch = Guid.NewGuid();
        ctx.Reviews.AddRange(
            new Review
            {
                CompanyId = Guid.NewGuid(), BranchId = branch, Source = "google",
                ExternalId = null, CompositeKey = "google:b:dt:1", RawText = "x",
                ReviewDate = DateTimeOffset.UtcNow
            },
            new Review
            {
                CompanyId = Guid.NewGuid(), BranchId = branch, Source = "google",
                ExternalId = null, CompositeKey = "google:b:dt:2", RawText = "y",
                ReviewDate = DateTimeOffset.UtcNow
            });

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Llm_result_unique_constraint_blocks_duplicate_review_job_pair()
    {
        var ctx = _pg.NewDbContext();
        await ResetData(ctx);

        var review = new Review
        {
            CompanyId = Guid.NewGuid(), BranchId = Guid.NewGuid(),
            Source = "yandex", CompositeKey = $"k:{Guid.NewGuid():N}",
            RawText = "x", ReviewDate = DateTimeOffset.UtcNow
        };
        ctx.Reviews.Add(review);
        await ctx.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        ctx.ReviewLlmResults.Add(new ReviewLlmResult
        {
            ReviewId = review.Id, AnalysisJobId = jobId,
            OverallSentiment = "позитивный", OverallConfidence = 0.9
        });
        await ctx.SaveChangesAsync();

        ctx.ReviewLlmResults.Add(new ReviewLlmResult
        {
            ReviewId = review.Id, AnalysisJobId = jobId,
            OverallSentiment = "негативный", OverallConfidence = 0.5
        });

        var act = async () => await ctx.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Where(ex => ex.SqlState == "23505");
    }

    [Fact]
    public async Task Connection_factory_returns_open_connection()
    {
        var factory = new NpgsqlConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ProcessingDb"] = _pg.ConnectionString
                })
                .Build());

        await using var conn = await factory.OpenAsync();
        var one = await conn.QuerySingleAsync<int>("SELECT 1");
        one.Should().Be(1);
    }

    [Fact]
    public async Task AnalysisJob_jsonb_collection_progress_round_trips()
    {
        var ctx = _pg.NewDbContext();
        await ResetJobs(ctx);

        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            Status = AnalysisJobStatus.Collecting,
            CollectionProgress = new Dictionary<string, CollectionProgressEntry>
            {
                ["yandex"] = new() { Status = "running", Progress = 60, TaskId = Guid.NewGuid() },
                ["2gis"]   = new() { Status = "completed", Progress = 100, ReviewCount = 142, S3Url = "s3://x/y" }
            }
        };
        ctx.AnalysisJobs.Add(job);
        await ctx.SaveChangesAsync();

        // Прочитаем через свежий DbContext, без change-tracker-кэша.
        await using var fresh = _pg.NewDbContext();
        var loaded = await fresh.AnalysisJobs.SingleAsync(j => j.Id == job.Id);
        loaded.Status.Should().Be(AnalysisJobStatus.Collecting);
        loaded.CollectionProgress.Should().HaveCount(2);
        loaded.CollectionProgress["yandex"].Progress.Should().Be(60);
        loaded.CollectionProgress["2gis"].ReviewCount.Should().Be(142);
    }

    private static Task ResetData(ProcessingDbContext ctx) =>
        ctx.Database.ExecuteSqlRawAsync("DELETE FROM review_llm_results; DELETE FROM reviews;");

    private static Task ResetJobs(ProcessingDbContext ctx) =>
        ctx.Database.ExecuteSqlRawAsync("DELETE FROM analysis_jobs;");
}
