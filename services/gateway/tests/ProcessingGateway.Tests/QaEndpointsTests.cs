using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Domain;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

/// Smoke-тесты QA-эндпоинтов. Полный pipeline-роутинг через брокер требует Pipeline
/// Collection — здесь покрываем DB- и S3-стороны, что доступны без RabbitMQ.
[Collection("Pipeline")]
public class QaEndpointsTests : IAsyncLifetime
{
    private readonly PgFixture _pg;
    private readonly MinioFixture _minio;
    private readonly RabbitFixture _rabbit;
    private ProcessingGatewayFactory _factory = null!;

    public QaEndpointsTests(PgFixture pg, MinioFixture minio, RabbitFixture rabbit)
    {
        _pg = pg;
        _minio = minio;
        _rabbit = rabbit;
    }

    public Task InitializeAsync()
    {
        _factory = new ProcessingGatewayFactory(new ProcessingGatewayFactory.Settings
        {
            ConnectionString = _pg.ConnectionString,
            S3Endpoint = _minio.Endpoint,
            S3AccessKey = _minio.AccessKey,
            S3SecretKey = _minio.SecretKey,
            S3BucketName = MinioFixture.BucketName,
            RabbitHost = _rabbit.Host,
            RabbitPort = _rabbit.Port,
            RabbitUser = _rabbit.Username,
            RabbitPass = _rabbit.Password
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetSnapshot_returns_full_job_state_or_404()
    {
        // Seed как Pending чтобы ParserPoller (фоновый) не подхватил job и не двинул статус
        // во время теста. Сценарий «cancel из Collecting» проверять напрямую опасно — гонка.
        var jobId = await SeedJobAsync(AnalysisJobStatus.Pending);
        var client = _factory.CreateClient();

        var ok = await client.GetAsync($"/api/qa/analyses/{jobId}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync()).Should()
            .Contain("collection_progress").And.Contain("created_at");

        var notFound = await client.GetAsync($"/api/qa/analyses/{Guid.NewGuid()}");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_marks_job_failed_with_reason()
    {
        var jobId = await SeedJobAsync(AnalysisJobStatus.Pending);
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/qa/analyses/{jobId}/cancel?reason=manual+test", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var ctx = _pg.NewDbContext();
        var job = await ctx.AnalysisJobs.SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AnalysisJobStatus.Failed);
        job.Error.Should().Be("manual test");
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_rejects_jobs_in_terminal_status()
    {
        var jobId = await SeedJobAsync(AnalysisJobStatus.Completed);
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/qa/analyses/{jobId}/cancel", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task LlmInject_writes_output_and_publishes_finished()
    {
        var jobId = await SeedJobAsync(AnalysisJobStatus.SentToLlm);

        var output = new LlmOutput(
            SchemaVersion: "1.0",
            AnalysisJobId: jobId,
            Recommendation: "via inject",
            ProcessedReview: new[]
            {
                new LlmProcessedReview(1, "normal", Array.Empty<string>(),
                    "positive", 0.9, false, 0.05, Array.Empty<string>())
            });

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/qa/llm/inject/{jobId}", output);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // output.json должен оказаться в MinIO
        using var s3 = _minio.CreateClient();
        using var resp = await s3.GetObjectAsync(MinioFixture.BucketName, $"{jobId}/output.json");
        using var reader = new StreamReader(resp.ResponseStream);
        var json = await reader.ReadToEndAsync();
        json.Should().Contain("via inject").And.Contain("\"review_id\":1");
    }

    [Fact]
    public async Task LlmTimeout_shifts_sent_at_into_past()
    {
        var jobId = await SeedJobAsync(AnalysisJobStatus.SentToLlm, sentAt: DateTimeOffset.UtcNow);
        var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/qa/llm/timeout/{jobId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var ctx = _pg.NewDbContext();
        var job = await ctx.AnalysisJobs.SingleAsync(j => j.Id == jobId);
        job.SentAt.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task Jobs_blobs_listing_returns_uploaded_keys()
    {
        var jobId = Guid.NewGuid();
        using var s3 = _minio.CreateClient();
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = MinioFixture.BucketName,
            Key = $"{jobId}/raw/yandex.json",
            ContentBody = "{}"
        });
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = MinioFixture.BucketName,
            Key = $"{jobId}/input.json",
            ContentBody = "{}"
        });

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/qa/jobs/{jobId}/blobs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"{jobId}/raw/yandex.json");
        body.Should().Contain($"{jobId}/input.json");
    }

    [Fact]
    public async Task Health_dependencies_returns_per_check_status()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/qa/health/dependencies");
        var raw = await response.Content.ReadAsStringAsync();

        var doc = System.Text.Json.JsonDocument.Parse(raw);
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => new
            {
                name = c.GetProperty("name").GetString(),
                ok = c.GetProperty("ok").GetBoolean(),
                error = c.TryGetProperty("error", out var e) ? e.GetString() : null
            }).ToList();

        // Все три пробы присутствуют
        checks.Select(c => c.name).Should().BeEquivalentTo(new[] { "postgres", "s3", "parser" });

        // postgres и s3 должны быть зелёными (PgFixture + MinioFixture реальные).
        // Если что-то не так — тест ВЫПИШЕТ JSON для отладки.
        var postgres = checks.Single(c => c.name == "postgres");
        postgres.ok.Should().BeTrue($"postgres check failed: {postgres.error ?? "(no error)"}; raw={raw}");

        var s3 = checks.Single(c => c.name == "s3");
        s3.ok.Should().BeTrue($"s3 check failed: {s3.error ?? "(no error)"}; raw={raw}");

        // parser ok=false ожидаемо — stub-host недоступен.
    }

    private async Task<Guid> SeedJobAsync(AnalysisJobStatus status, DateTimeOffset? sentAt = null)
    {
        var jobId = Guid.NewGuid();
        await using var ctx = _pg.NewDbContext();
        ctx.AnalysisJobs.Add(new AnalysisJob
        {
            Id = jobId,
            CompanyId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SentAt = sentAt,
            CollectionProgress = new Dictionary<string, CollectionProgressEntry>
            {
                ["yandex"] = new() { Status = "completed", Progress = 100, ReviewCount = 4 }
            }
        });
        await ctx.SaveChangesAsync();
        return jobId;
    }
}
