using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessingGateway.Infrastructure.Parser;
using ProcessingGateway.Infrastructure.Parser.Contracts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ProcessingGateway.Tests;

public class ParserHttpClientTests : IAsyncLifetime
{
    private WireMockServer _wireMock = null!;
    private HttpClient _http = null!;
    private ParserHttpClient _client = null!;

    public Task InitializeAsync()
    {
        _wireMock = WireMockServer.Start();
        _http = new HttpClient { BaseAddress = new Uri(_wireMock.Url!.TrimEnd('/') + "/") };
        _client = new ParserHttpClient(_http, NullLogger<ParserHttpClient>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _wireMock.Stop();
        _wireMock.Dispose();
        _http.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartCollection_sends_snake_case_body_and_returns_task_id()
    {
        var taskId = Guid.NewGuid();
        _wireMock
            .Given(Request.Create()
                .WithPath("/api/collection-tasks")
                .UsingPost()
                .WithHeader("Content-Type", "application/json*"))
            .RespondWith(Response.Create()
                .WithStatusCode(202)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"task_id":"{{taskId}}"}"""));

        var request = new StartCollectionRequest(
            JobId: Guid.NewGuid(),
            CompanyId: Guid.NewGuid(),
            Source: "yandex",
            DateFrom: DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            DateTo: null,
            Branches: new[]
            {
                new BranchTargetDto(Guid.NewGuid(), "1124715036", "https://yandex.ru/maps/org/.../1124715036/")
            });

        var actual = await _client.StartCollectionAsync(request);

        actual.Should().Be(taskId);

        // Проверяем, что мы отправили snake_case-тело (как ожидает Parser).
        var sent = _wireMock.LogEntries.Should().ContainSingle().Which;
        var body = sent.RequestMessage!.Body
            ?? throw new InvalidOperationException("WireMock не зафиксировал тело запроса");
        body.Should().Contain("\"job_id\":");
        body.Should().Contain("\"company_id\":");
        body.Should().Contain("\"date_from\":");
        body.Should().NotContain("\"DateFrom\"");
        body.Should().Contain("\"branch_id\":");
        body.Should().Contain("\"external_id\":");
        body.Should().Contain("\"external_url\":");
        // null DateTo не должен лететь (DefaultIgnoreCondition.WhenWritingNull)
        body.Should().NotContain("\"date_to\":");
    }

    [Fact]
    public async Task GetStatus_parses_completed_response_with_s3_url()
    {
        var taskId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        _wireMock
            .Given(Request.Create()
                .WithPath($"/api/collection-tasks/{taskId}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "task_id": "{{taskId}}",
                    "status": "completed",
                    "source": "yandex",
                    "progress": 1.0,
                    "review_count": 47,
                    "s3_url": "s3://obratka-jobs/{{jobId}}/raw/yandex.json",
                    "error": null
                }
                """));

        var status = await _client.GetStatusAsync(taskId);

        status.TaskId.Should().Be(taskId);
        status.Status.Should().Be("completed");
        status.Source.Should().Be("yandex");
        status.Progress.Should().Be(1.0);
        status.ReviewCount.Should().Be(47);
        status.S3Url.Should().Be($"s3://obratka-jobs/{jobId}/raw/yandex.json");
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_parses_running_response_without_s3_url()
    {
        var taskId = Guid.NewGuid();
        _wireMock
            .Given(Request.Create()
                .WithPath($"/api/collection-tasks/{taskId}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "task_id": "{{taskId}}",
                    "status": "running",
                    "source": "2gis",
                    "progress": 0.6,
                    "review_count": null,
                    "s3_url": null,
                    "error": null
                }
                """));

        var status = await _client.GetStatusAsync(taskId);

        status.Status.Should().Be("running");
        status.Progress.Should().Be(0.6);
        status.ReviewCount.Should().BeNull();
        status.S3Url.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_throws_ParserTaskNotFoundException_on_404()
    {
        var taskId = Guid.NewGuid();
        _wireMock
            .Given(Request.Create().WithPath($"/api/collection-tasks/{taskId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var act = async () => await _client.GetStatusAsync(taskId);

        await act.Should().ThrowAsync<ParserTaskNotFoundException>()
            .WithMessage($"Parser task {taskId} not found");
    }

    [Fact]
    public async Task GetStatus_propagates_5xx_as_HttpRequestException()
    {
        var taskId = Guid.NewGuid();
        _wireMock
            .Given(Request.Create().WithPath($"/api/collection-tasks/{taskId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var act = async () => await _client.GetStatusAsync(taskId);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task StartCollection_propagates_400_BadRequest()
    {
        _wireMock
            .Given(Request.Create().WithPath("/api/collection-tasks").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBody("""{"error":"Unknown source: 'foo'"}"""));

        var request = new StartCollectionRequest(
            JobId: Guid.NewGuid(), CompanyId: Guid.NewGuid(),
            Source: "foo", DateFrom: null, DateTo: null,
            Branches: new[] { new BranchTargetDto(Guid.NewGuid(), "x", "y") });

        var act = async () => await _client.StartCollectionAsync(request);
        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
