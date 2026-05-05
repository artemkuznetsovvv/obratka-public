using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Infrastructure.Storage;
using ProcessingGateway.LlmStub;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Minio")]
public class LlmStubTests : IAsyncLifetime
{
    private readonly MinioFixture _minio;
    private ITestHarness _harness = null!;
    private IServiceProvider _services = null!;

    public LlmStubTests(MinioFixture minio) => _minio = minio;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["S3:BucketName"] = MinioFixture.BucketName
                }).Build());
        services.AddSingleton<Amazon.S3.IAmazonS3>(_minio.CreateClient());
        services.AddSingleton<IJobBlobStorage, S3JobBlobStorage>();

        services.AddMassTransitTestHarness(x => x.AddConsumer<LlmRequestMessageConsumer>());

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
    public async Task Stub_reads_input_synthesizes_output_and_publishes_finished_result()
    {
        var jobId = Guid.NewGuid();
        var blob = _services.GetRequiredService<IJobBlobStorage>();

        var input = new LlmInput(
            SchemaVersion: "1.0",
            AnalysisJobId: jobId,
            CompanyId: Guid.NewGuid(),
            Reviews: new[]
            {
                new LlmInputReview(1, "Все отлично 👌, рекомендую!", "yandex",
                    DateTimeOffset.UtcNow, 5, Guid.NewGuid(), "ru"),
                new LlmInputReview(2, "Плохое обслуживание, не рекомендую",
                    "2gis", DateTimeOffset.UtcNow, 1, Guid.NewGuid(), "ru"),
                new LlmInputReview(3, "Что-то нейтральное", "google",
                    DateTimeOffset.UtcNow, 3, Guid.NewGuid(), null)
            });
        await blob.WriteInputAsync(jobId, input);

        await _harness.Bus.Publish(new LlmRequestMessage(
            AnalysisJobId: jobId,
            CompanyId: input.CompanyId,
            PayloadUrl: $"s3://{MinioFixture.BucketName}/{jobId}/input.json",
            ReviewCount: 3,
            SchemaVersion: "1.0",
            CallbackQueue: "llm.results"));

        var consumed = _harness.GetConsumerHarness<LlmRequestMessageConsumer>();
        (await consumed.Consumed.Any<LlmRequestMessage>()).Should().BeTrue();

        // output.json должен появиться в MinIO
        var output = await blob.ReadOutputAsync(jobId);
        output.AnalysisJobId.Should().Be(jobId);
        output.SchemaVersion.Should().Be("1.0");
        output.ProcessedReview.Should().HaveCount(3);

        var byReviewId = output.ProcessedReview.ToDictionary(p => p.ReviewId);
        byReviewId[1].Sentiment.Should().BeOneOf("positive", "very_positive");
        byReviewId[2].Sentiment.Should().BeOneOf("negative", "very_negative");
        byReviewId[3].Sentiment.Should().Be("neutral");

        // Recommendation должна содержать stub-маркер
        output.Recommendation.Should().Contain("(stub)");

        // Опубликован ли LlmResultMessage finished?
        (await _harness.Published.Any<LlmResultMessage>(
            x => x.Context.Message.AnalysisJobId == jobId
              && x.Context.Message.Status == "finished"
              && x.Context.Message.ResultUrl != null))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Stub_publishes_failed_when_input_missing()
    {
        var jobId = Guid.NewGuid();

        await _harness.Bus.Publish(new LlmRequestMessage(
            AnalysisJobId: jobId,
            CompanyId: Guid.NewGuid(),
            PayloadUrl: $"s3://{MinioFixture.BucketName}/{jobId}/input.json",  // нет такого ключа
            ReviewCount: 0,
            SchemaVersion: "1.0",
            CallbackQueue: "llm.results"));

        var consumed = _harness.GetConsumerHarness<LlmRequestMessageConsumer>();
        await consumed.Consumed.Any<LlmRequestMessage>();

        (await _harness.Published.Any<LlmResultMessage>(
            x => x.Context.Message.AnalysisJobId == jobId
              && x.Context.Message.Status == "failed"
              && x.Context.Message.Error != null))
            .Should().BeTrue();
    }
}
