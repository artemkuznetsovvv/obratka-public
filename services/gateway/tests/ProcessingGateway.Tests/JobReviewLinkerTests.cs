using Dapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ProcessingGateway.Application.Ingestion;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Postgres")]
public class JobReviewLinkerTests
{
    private readonly PgFixture _pg;

    public JobReviewLinkerTests(PgFixture pg) => _pg = pg;

    private NpgsqlConnectionFactory Factory() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProcessingDb"] = _pg.ConnectionString
            })
            .Build());

    [Fact]
    public async Task LinkAsync_creates_one_row_per_review_for_first_job()
    {
        await ResetData();
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();
        await new RawReviewBulkInserter(Factory()).InsertAsync(entities);

        var jobId = Guid.NewGuid();
        var linker = new JobReviewLinker(Factory());
        var inserted = await linker.LinkAsync(jobId, entities.Select(e => e.CompositeKey).ToList());

        inserted.Should().Be(payload.Reviews.Count);

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM analysis_job_reviews WHERE analysis_job_id = @JobId",
            new { JobId = jobId });
        count.Should().Be(payload.Reviews.Count);
    }

    [Fact]
    public async Task LinkAsync_is_idempotent_on_repeated_call_with_same_inputs()
    {
        await ResetData();
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();
        await new RawReviewBulkInserter(Factory()).InsertAsync(entities);

        var jobId = Guid.NewGuid();
        var linker = new JobReviewLinker(Factory());
        var keys = entities.Select(e => e.CompositeKey).ToList();

        var first = await linker.LinkAsync(jobId, keys);
        var second = await linker.LinkAsync(jobId, keys);

        first.Should().Be(payload.Reviews.Count, "первый прогон создаёт связи");
        second.Should().Be(0, "дубль (jobId, reviewId) ловится PK на analysis_job_reviews");
    }

    [Fact]
    public async Task Same_review_can_be_linked_to_multiple_jobs()
    {
        // Это ключевой тест варианта B: повторный анализ той же компании должен
        // включать те же отзывы (composite_key совпадает, ON CONFLICT в reviews не
        // перевставит, но новая запись в analysis_job_reviews появляется).
        await ResetData();
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();
        await new RawReviewBulkInserter(Factory()).InsertAsync(entities);

        var keys = entities.Select(e => e.CompositeKey).ToList();
        var linker = new JobReviewLinker(Factory());

        var job1 = Guid.NewGuid();
        var job2 = Guid.NewGuid();
        await linker.LinkAsync(job1, keys);
        await linker.LinkAsync(job2, keys);

        await using var ctx = _pg.NewDbContext();

        // Каждый job содержит все отзывы.
        (await ctx.AnalysisJobReviews.CountAsync(l => l.AnalysisJobId == job1))
            .Should().Be(payload.Reviews.Count);
        (await ctx.AnalysisJobReviews.CountAsync(l => l.AnalysisJobId == job2))
            .Should().Be(payload.Reviews.Count);

        // А каждый отзыв привязан ровно к двум jobs.
        var reviewIds = await ctx.Reviews.Select(r => r.Id).ToListAsync();
        foreach (var id in reviewIds)
        {
            (await ctx.AnalysisJobReviews.CountAsync(l => l.ReviewId == id))
                .Should().Be(2);
        }
    }

    [Fact]
    public async Task LinkAsync_silently_skips_unknown_composite_keys()
    {
        // Сценарий: ParserPoller передал keys, но один отзыв ещё не вставлен (race
        // или ошибка). Linker не должен падать — просто не свяжет несуществующий.
        await ResetData();
        var payload = FixtureLoader.LoadRaw("yandex");
        var entities = payload.Reviews.Take(2).Select(r => RawReviewMapper.ToEntity(payload, r)).ToList();
        await new RawReviewBulkInserter(Factory()).InsertAsync(entities);

        var keys = entities.Select(e => e.CompositeKey)
            .Append("yandex:nonexistent-key:ext:xxx")
            .ToList();

        var inserted = await new JobReviewLinker(Factory()).LinkAsync(Guid.NewGuid(), keys);

        inserted.Should().Be(2, "несуществующий ключ молча пропущен");
    }

    [Fact]
    public async Task LinkAsync_returns_zero_for_empty_input()
    {
        var inserted = await new JobReviewLinker(Factory()).LinkAsync(
            Guid.NewGuid(),
            Array.Empty<string>());

        inserted.Should().Be(0);
    }

    private async Task ResetData()
    {
        await using var ctx = _pg.NewDbContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DELETE FROM analysis_job_reviews;
            DELETE FROM review_llm_results;
            DELETE FROM reviews;");
    }
}
