using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Application.Pipeline;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Storage;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("PgMinio")]
public class LlmResultIngestorTests : IAsyncLifetime
{
    private readonly PgFixture _pg;
    private readonly MinioFixture _minio;
    private ITestHarness _harness = null!;
    private IServiceProvider _services = null!;

    public LlmResultIngestorTests(PgFixture pg, MinioFixture minio)
    {
        _pg = pg;
        _minio = minio;
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddDbContext<ProcessingDbContext>(o =>
            o.UseNpgsql(_pg.ConnectionString).UseSnakeCaseNamingConvention());

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ProcessingDb"] = _pg.ConnectionString,
                ["S3:BucketName"] = MinioFixture.BucketName
            }).Build());
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<ReviewLlmResultBulkInserter>();
        services.AddSingleton<Amazon.S3.IAmazonS3>(_minio.CreateClient());
        services.AddSingleton<IJobBlobStorage, S3JobBlobStorage>();
        services.AddScoped<LlmResultIngestor>();

        services.AddMassTransitTestHarness();

        _services = services.BuildServiceProvider();
        _harness = _services.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        if (_services is IAsyncDisposable disposable) await disposable.DisposeAsync();
    }

    [Fact]
    public async Task IngestFinished_saves_results_and_advances_to_computing_aggregates()
    {
        var jobId = Guid.NewGuid();
        await SeedJobAsync(jobId, AnalysisJobStatus.SentToLlm);
        var (reviewIds, _) = await SeedReviewsAsync(jobId, count: 3);

        var output = new LlmOutput(
            SchemaVersion: "1.0",
            AnalysisJobId: jobId,
            Recommendation: "Сделать лучше",
            ProcessedReview: reviewIds.Select((id, i) => new LlmProcessedReview(
                ReviewId: id,
                FakeStatus: "normal",
                FakeReasonTags: Array.Empty<string>(),
                Sentiment: i == 0 ? "positive" : "neutral",
                SentimentConfidence: 0.8,
                IsSpam: false,
                SpamConfidence: 0.05,
                Topics: i == 0 ? new[] { "сервис" } : Array.Empty<string>())).ToList());

        var blob = _services.GetRequiredService<IJobBlobStorage>();
        await blob.WriteOutputAsync(jobId, output);

        using var scope = _services.CreateScope();
        var ingestor = scope.ServiceProvider.GetRequiredService<LlmResultIngestor>();
        await ingestor.IngestFinishedAsync(jobId, $"s3://{MinioFixture.BucketName}/{jobId}/output.json");

        await using var ctx = _pg.NewDbContext();
        var job = await ctx.AnalysisJobs.SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AnalysisJobStatus.ComputingAggregates);
        job.Recommendation.Should().Be("Сделать лучше");
        job.ResultUrl.Should().Be($"s3://{MinioFixture.BucketName}/{jobId}/output.json");

        var saved = await ctx.ReviewLlmResults.Where(r => r.AnalysisJobId == jobId).ToListAsync();
        saved.Should().HaveCount(3);
        saved.Should().Contain(r => r.Sentiment == "positive" && r.Topics.Contains("сервис"));

        // Опубликован ли AnalysisCompletedEvent?
        (await _harness.Published.Any<AnalysisCompletedEvent>(
            x => x.Context.Message.AnalysisJobId == jobId
              && x.Context.Message.Status == AnalysisCompletionStatus.CompletedPendingAggregates))
            .Should().BeTrue();
    }

    [Fact]
    public async Task IngestFinished_marks_partial_when_any_source_failed()
    {
        var jobId = Guid.NewGuid();
        await SeedJobAsync(jobId, AnalysisJobStatus.SentToLlm, withFailedSource: true);
        var (reviewIds, _) = await SeedReviewsAsync(jobId, count: 1);

        var output = new LlmOutput("1.0", jobId, "rec",
            new[] { MinimalProcessed(reviewIds[0]) });
        await _services.GetRequiredService<IJobBlobStorage>().WriteOutputAsync(jobId, output);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LlmResultIngestor>()
            .IngestFinishedAsync(jobId, $"s3://{MinioFixture.BucketName}/{jobId}/output.json");

        (await _harness.Published.Any<AnalysisCompletedEvent>(
            x => x.Context.Message.AnalysisJobId == jobId
              && x.Context.Message.Status == AnalysisCompletionStatus.Partial))
            .Should().BeTrue();
    }

    [Fact]
    public async Task IngestFailed_sets_status_failed_and_publishes_event()
    {
        var jobId = Guid.NewGuid();
        await SeedJobAsync(jobId, AnalysisJobStatus.SentToLlm);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LlmResultIngestor>()
            .IngestFailedAsync(jobId, "LLM blew up");

        await using var ctx = _pg.NewDbContext();
        var job = await ctx.AnalysisJobs.SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AnalysisJobStatus.Failed);
        job.Error.Should().Be("LLM blew up");
        job.CompletedAt.Should().NotBeNull();

        (await _harness.Published.Any<AnalysisCompletedEvent>(
            x => x.Context.Message.AnalysisJobId == jobId
              && x.Context.Message.Status == AnalysisCompletionStatus.Failed))
            .Should().BeTrue();
    }

    [Fact]
    public async Task IngestFinished_is_idempotent_on_repeated_call()
    {
        var jobId = Guid.NewGuid();
        await SeedJobAsync(jobId, AnalysisJobStatus.SentToLlm);
        var (reviewIds, _) = await SeedReviewsAsync(jobId, count: 2);

        var output = new LlmOutput("1.0", jobId, "rec",
            reviewIds.Select(MinimalProcessed).ToList());
        await _services.GetRequiredService<IJobBlobStorage>().WriteOutputAsync(jobId, output);

        using var scope = _services.CreateScope();
        var ingestor = scope.ServiceProvider.GetRequiredService<LlmResultIngestor>();

        await ingestor.IngestFinishedAsync(jobId, $"s3://{MinioFixture.BucketName}/{jobId}/output.json");
        await ingestor.IngestFinishedAsync(jobId, $"s3://{MinioFixture.BucketName}/{jobId}/output.json");

        await using var ctx = _pg.NewDbContext();
        (await ctx.ReviewLlmResults.CountAsync(r => r.AnalysisJobId == jobId))
            .Should().Be(2, "повторный ingest не должен задвоить строки");
    }

    // --- helpers ---

    private static LlmProcessedReview MinimalProcessed(long reviewId) => new(
        ReviewId: reviewId,
        FakeStatus: "normal",
        FakeReasonTags: Array.Empty<string>(),
        Sentiment: "neutral",
        SentimentConfidence: 0.5,
        IsSpam: false,
        SpamConfidence: 0.05,
        Topics: Array.Empty<string>());

    private async Task SeedJobAsync(Guid jobId, AnalysisJobStatus status, bool withFailedSource = false)
    {
        await using var ctx = _pg.NewDbContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DELETE FROM review_llm_results;
            DELETE FROM analysis_job_reviews;
            DELETE FROM reviews;
            DELETE FROM analysis_jobs;");

        var progress = new Dictionary<string, CollectionProgressEntry>
        {
            ["yandex"] = new() { Status = "completed", Progress = 100, ReviewCount = 5 }
        };
        if (withFailedSource)
            progress["2gis"] = new() { Status = "failed", Progress = 0, Error = "anti-bot" };

        ctx.AnalysisJobs.Add(new AnalysisJob
        {
            Id = jobId,
            CompanyId = Guid.NewGuid(),
            Status = status,
            CollectionProgress = progress,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            SentAt = DateTimeOffset.UtcNow.AddSeconds(-30)
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(List<long> Ids, List<string> CompositeKeys)> SeedReviewsAsync(Guid jobId, int count)
    {
        await using var ctx = _pg.NewDbContext();
        var entities = Enumerable.Range(1, count).Select(i => new Review
        {
            CompanyId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            Source = "yandex",
            CompositeKey = $"yandex-test-{jobId:N}-{i}",
            RawText = $"text-{i}",
            ReviewDate = DateTimeOffset.UtcNow.AddHours(-i),
            CollectedAt = DateTimeOffset.UtcNow,
            Stars = 5
        }).ToList();
        ctx.Reviews.AddRange(entities);
        await ctx.SaveChangesAsync();

        ctx.AnalysisJobReviews.AddRange(entities.Select(e =>
            new AnalysisJobReview { AnalysisJobId = jobId, ReviewId = e.Id }));
        await ctx.SaveChangesAsync();

        return (entities.Select(e => e.Id).ToList(), entities.Select(e => e.CompositeKey).ToList());
    }
}

[CollectionDefinition("PgMinio")]
public class PgMinioCollection : ICollectionFixture<PgFixture>, ICollectionFixture<MinioFixture> { }
