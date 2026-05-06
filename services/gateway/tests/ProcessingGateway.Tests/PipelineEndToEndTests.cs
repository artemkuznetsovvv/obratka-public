using System.Net;
using System.Net.Http.Json;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Api.Qa;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ProcessingGateway.Tests;

/// Сквозной happy-path Этапа 5:
///   POST /api/qa/analyses
///     → MassTransit → StartAnalysisCommandConsumer
///       → Parser stub: POST /api/collection-tasks → 202 + taskId
///       → status=collecting в БД
///     ParserPoller (1с интервал в тесте):
///       → GET /api/collection-tasks/{taskId} → completed + s3Url
///       → читает raw/yandex.json (заранее загруженную в MinIO)
///       → bulk insert reviews + link к job
///     AnalysisOrchestrator → LlmDispatcher
///       → пишет input.json в MinIO
///       → publish LlmRequestMessage
///       → status=sent_to_llm
///
/// Проверяем итоговую БД (status, payload_url) и MinIO (input.json, корректность содержимого).
[Collection("Pipeline")]
public class PipelineEndToEndTests : IAsyncLifetime
{
    private readonly PgFixture _pg;
    private readonly MinioFixture _minio;
    private readonly RabbitFixture _rabbit;

    private WireMockServer _parserStub = null!;
    private ProcessingGatewayFactory _factory = null!;

    public PipelineEndToEndTests(PgFixture pg, MinioFixture minio, RabbitFixture rabbit)
    {
        _pg = pg;
        _minio = minio;
        _rabbit = rabbit;
    }

    public Task InitializeAsync()
    {
        _parserStub = WireMockServer.Start();
        _factory = new ProcessingGatewayFactory(new ProcessingGatewayFactory.Settings
        {
            ConnectionString = _pg.ConnectionString,
            ParserBaseUrl = _parserStub.Url!,
            S3Endpoint = _minio.Endpoint,
            S3AccessKey = _minio.AccessKey,
            S3SecretKey = _minio.SecretKey,
            S3BucketName = MinioFixture.BucketName,
            RabbitHost = _rabbit.Host,
            RabbitPort = _rabbit.Port,
            RabbitUser = _rabbit.Username,
            RabbitPass = _rabbit.Password,
            PollIntervalSeconds = 1   // ускоряем для теста
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _parserStub.Stop();
        _parserStub.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact(Timeout = 60_000)]
    public async Task HappyPath_yandex_single_source_drives_job_to_sent_to_llm()
    {
        var jobId = Guid.NewGuid();
        // companyId совпадает с тем, что зашит в Fixtures/raw/yandex.json:
        // RawReviewMapper берёт company_id из payload Parser-а (он возвращает то, что мы ему
        // отправили в StartCollectionRequest). В реальности эти два значения совпадают;
        // в тесте мы заранее заставляем их совпадать.
        var companyId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var branchId = Guid.Parse("e309d01c-c44b-4412-9455-3714ca056549");
        var parserTaskId = Guid.NewGuid();

        // 1. Загружаем yandex-фикстуру в MinIO под ожидаемый ключ.
        await UploadFixtureAsync(jobId, "yandex");

        // 2. Парсер-стуб: создание таска + статус.
        _parserStub
            .Given(Request.Create().WithPath("/api/collection-tasks").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(202)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"task_id":"{{parserTaskId}}"}"""));

        _parserStub
            .Given(Request.Create().WithPath($"/api/collection-tasks/{parserTaskId}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "task_id": "{{parserTaskId}}",
                    "status": "completed",
                    "source": "yandex",
                    "progress": 1.0,
                    "review_count": 4,
                    "s3_url": "s3://{{MinioFixture.BucketName}}/{{jobId}}/raw/yandex.json",
                    "error": null
                }
                """));

        // 3. POST /api/qa/analyses с predefined jobId.
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/qa/analyses",
            new QaAnalysesController.StartAnalysisQaRequest(
                CompanyId: companyId,
                DateFrom: null,
                DateTo: null,
                Branches: new[]
                {
                    new BranchSpec(branchId, "yandex", "1124715036",
                        "https://yandex.ru/maps/org/.../1124715036/")
                },
                AnalysisJobId: jobId));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<QaAnalysesController.StartAnalysisQaResponse>();
        body!.AnalysisJobId.Should().Be(jobId);

        // 4. Ждём пока ParserPoller + LlmDispatcher переведут job в sent_to_llm.
        var finalJob = await WaitForStatusAsync(jobId, AnalysisJobStatus.SentToLlm,
            TimeSpan.FromSeconds(45));

        finalJob.PayloadUrl.Should().Be($"s3://{MinioFixture.BucketName}/{jobId}/input.json");
        finalJob.SentAt.Should().NotBeNull();
        finalJob.ReviewCount.Should().Be(4);

        // 5. Проверяем входной payload в MinIO.
        using var s3 = _minio.CreateClient();
        using var inputResp = await s3.GetObjectAsync(MinioFixture.BucketName, $"{jobId}/input.json");
        using var reader = new StreamReader(inputResp.ResponseStream);
        var inputJson = await reader.ReadToEndAsync();

        inputJson.Should().Contain("\"schema_version\":\"2.0\"");
        inputJson.Should().Contain($"\"analysis_job_id\":\"{jobId}\"");
        inputJson.Should().Contain($"\"company_id\":\"{companyId}\"");
        // 4 отзыва из фикстуры
        var reviewIdMatches = System.Text.RegularExpressions.Regex.Matches(inputJson, "\"review_id\":\\d+");
        reviewIdMatches.Count.Should().Be(4);

        // 6. БД: reviews + связи существуют.
        await using var ctx = _pg.NewDbContext();
        var dbReviewCount = await ctx.Reviews.CountAsync(r => r.CompanyId == companyId);
        dbReviewCount.Should().Be(4);

        var linkCount = await ctx.AnalysisJobReviews.CountAsync(l => l.AnalysisJobId == jobId);
        linkCount.Should().Be(4);
    }

    [Fact(Timeout = 60_000)]
    public async Task AllSourcesFailed_drives_job_to_failed_without_llm_dispatch()
    {
        var jobId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var parserTaskId = Guid.NewGuid();

        _parserStub
            .Given(Request.Create().WithPath("/api/collection-tasks").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(202)
                .WithBody($$"""{"task_id":"{{parserTaskId}}"}"""));

        _parserStub
            .Given(Request.Create().WithPath($"/api/collection-tasks/{parserTaskId}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody($$"""
                {
                    "task_id": "{{parserTaskId}}",
                    "status": "failed",
                    "source": "yandex",
                    "progress": 0.0,
                    "review_count": null,
                    "s3_url": null,
                    "error": "SmartCaptcha blocked all retries"
                }
                """));

        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/qa/analyses",
            new QaAnalysesController.StartAnalysisQaRequest(
                CompanyId: companyId,
                DateFrom: null, DateTo: null,
                Branches: new[]
                {
                    new BranchSpec(branchId, "yandex", "ext-fail", "url")
                },
                AnalysisJobId: jobId));

        var failedJob = await WaitForStatusAsync(jobId, AnalysisJobStatus.Failed,
            TimeSpan.FromSeconds(30));

        failedJob.Error.Should().Contain("All sources failed");
        failedJob.PayloadUrl.Should().BeNull("LLM не должен был быть вызван");

        // input.json не существует
        using var s3 = _minio.CreateClient();
        var act = async () => await s3.GetObjectAsync(MinioFixture.BucketName, $"{jobId}/input.json");
        await act.Should().ThrowAsync<Amazon.S3.AmazonS3Exception>();
    }

    private async Task<AnalysisJob> WaitForStatusAsync(Guid jobId, AnalysisJobStatus expected, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AnalysisJobStatus? lastSeen = null;

        while (sw.Elapsed < timeout)
        {
            await using var ctx = _pg.NewDbContext();
            var job = await ctx.AnalysisJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is not null)
            {
                lastSeen = job.Status;
                if (job.Status == expected) return job;
            }
            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Job {jobId} did not reach status {expected} within {timeout}. Last seen: {lastSeen?.ToString() ?? "<no row>"}");
    }

    private async Task UploadFixtureAsync(Guid jobId, string source)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "raw", $"{source}.json");
        var json = await File.ReadAllTextAsync(fixturePath);

        using var client = _minio.CreateClient();
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = MinioFixture.BucketName,
            Key = $"{jobId}/raw/{source}.json",
            ContentBody = json,
            ContentType = "application/json"
        });
    }
}
