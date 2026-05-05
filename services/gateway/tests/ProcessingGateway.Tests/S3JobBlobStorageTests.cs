using System.Text.Json;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Infrastructure.Storage;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Minio")]
public class S3JobBlobStorageTests
{
    private readonly MinioFixture _minio;

    public S3JobBlobStorageTests(MinioFixture minio) => _minio = minio;

    private S3JobBlobStorage NewStorage() => new(
        _minio.CreateClient(),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:BucketName"] = MinioFixture.BucketName
            })
            .Build(),
        NullLogger<S3JobBlobStorage>.Instance);

    [Fact]
    public async Task ReadRaw_by_jobId_and_source_round_trips_yandex_fixture()
    {
        var jobId = Guid.NewGuid();
        await UploadFixtureAsync(jobId, "yandex");

        var storage = NewStorage();
        var payload = await storage.ReadRawAsync(jobId, "yandex");

        payload.Source.Should().Be("yandex");
        payload.Reviews.Should().HaveCount(4);
        payload.Reviews.Should().Contain(r => r.Text.Contains("👌"),
            "UTF-8/эмодзи должны выживать round-trip через S3");
    }

    [Fact]
    public async Task ReadRaw_by_s3_url_works_for_full_path()
    {
        var jobId = Guid.NewGuid();
        await UploadFixtureAsync(jobId, "2gis");

        var storage = NewStorage();
        var payload = await storage.ReadRawAsync(
            $"s3://{MinioFixture.BucketName}/{jobId}/raw/2gis.json");

        payload.Source.Should().Be("2gis");
        payload.Reviews.Should().HaveCount(5);
    }

    [Fact]
    public async Task WriteInput_then_ReadOutput_full_llm_round_trip()
    {
        var jobId = Guid.NewGuid();

        var input = new LlmInput(
            SchemaVersion: "1.0",
            AnalysisJobId: jobId,
            CompanyId: Guid.NewGuid(),
            Reviews: new[]
            {
                new LlmInputReview(ReviewId: 1, Text: "Хорошо", Source: "yandex",
                    Date: DateTimeOffset.UtcNow, Stars: 5,
                    BranchId: Guid.NewGuid(), TextLanguage: "ru"),
                new LlmInputReview(ReviewId: 2, Text: "Плохо", Source: "2gis",
                    Date: DateTimeOffset.UtcNow, Stars: 1,
                    BranchId: Guid.NewGuid(), TextLanguage: null)
            });

        var storage = NewStorage();
        await storage.WriteInputAsync(jobId, input);

        // Симулируем, что LLM записал output.json в обычное место.
        var llmOutput = new LlmOutput(
            SchemaVersion: "1.0",
            AnalysisJobId: jobId,
            Recommendation: "Улучшить скорость доставки",
            ProcessedReview: new[]
            {
                new LlmProcessedReview(
                    ReviewId: 1, FakeStatus: "normal", FakeReasonTags: Array.Empty<string>(),
                    Sentiment: "positive", SentimentConfidence: 0.92, IsSpam: false,
                    SpamConfidence: 0.04, Topics: new[] { "сервис" }),
                new LlmProcessedReview(
                    ReviewId: 2, FakeStatus: "suspicious", FakeReasonTags: new[] { "однотипный текст" },
                    Sentiment: "negative", SentimentConfidence: 0.85, IsSpam: false,
                    SpamConfidence: 0.10, Topics: Array.Empty<string>())
            });

        await UploadOutputAsync(jobId, llmOutput);

        var loadedOutput = await storage.ReadOutputAsync(jobId);

        loadedOutput.AnalysisJobId.Should().Be(jobId);
        loadedOutput.Recommendation.Should().Be("Улучшить скорость доставки");
        loadedOutput.ProcessedReview.Should().HaveCount(2);
        loadedOutput.ProcessedReview[0].ReviewId.Should().Be(1);
        loadedOutput.ProcessedReview[0].Topics.Should().ContainSingle().Which.Should().Be("сервис");
        loadedOutput.ProcessedReview[1].FakeReasonTags.Should().Contain("однотипный текст");

        // Проверяем, что мы записали именно то, что хотели
        using var inputResp = await _minio.CreateClient()
            .GetObjectAsync(MinioFixture.BucketName, $"{jobId}/input.json");
        using var inputReader = new StreamReader(inputResp.ResponseStream);
        var inputJson = await inputReader.ReadToEndAsync();

        // ADR-004: ключ называется processedReview (camelCase), но input — snake_case.
        inputJson.Should().Contain("\"schema_version\":");
        inputJson.Should().Contain("\"analysis_job_id\":");
        inputJson.Should().Contain("\"company_id\":");
        inputJson.Should().Contain("\"review_id\":1");      // long как число, не строка
        inputJson.Should().Contain("\"branch_id\":");
        inputJson.Should().Contain("\"text_language\":\"ru\"");
        // null-text_language второго ревью не должен сериализоваться
        inputJson.Should().NotContain("\"text_language\":null");
    }

    [Fact]
    public async Task ReadOutput_correctly_handles_processedReview_camelCase_field()
    {
        // LLM по контракту ADR-004 пишет именно `processedReview` camelCase. Проверяем,
        // что JsonPropertyName-атрибут на LlmOutput ловит это, не требуя global naming policy.
        var jobId = Guid.NewGuid();
        var raw = $$"""
        {
          "schema_version": "1.0",
          "analysis_job_id": "{{jobId}}",
          "recommendation": "test",
          "processedReview": [
            {
              "review_id": 42,
              "fake_status": "fake",
              "fake_reason_tags": ["накрутка"],
              "sentiment": null,
              "sentiment_confidence": null,
              "is_spam": true,
              "spam_confidence": 0.99,
              "topics": []
            }
          ]
        }
        """;

        await UploadRawJsonAsync($"{jobId}/output.json", raw);

        var output = await NewStorage().ReadOutputAsync(jobId);

        output.ProcessedReview.Should().HaveCount(1);
        var processed = output.ProcessedReview[0];
        processed.ReviewId.Should().Be(42);
        processed.FakeStatus.Should().Be("fake");
        processed.FakeReasonTags.Should().ContainSingle().Which.Should().Be("накрутка");
        processed.Sentiment.Should().BeNull();
        processed.IsSpam.Should().BeTrue();
        processed.SpamConfidence.Should().Be(0.99);
    }

    private async Task UploadFixtureAsync(Guid jobId, string source)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "raw", $"{source}.json");
        var json = await File.ReadAllTextAsync(fixturePath);

        await UploadRawJsonAsync($"{jobId}/raw/{source}.json", json);
    }

    private async Task UploadOutputAsync(Guid jobId, LlmOutput output)
    {
        var json = JsonSerializer.Serialize(output);
        await UploadRawJsonAsync($"{jobId}/output.json", json);
    }

    private async Task UploadRawJsonAsync(string key, string json)
    {
        using var client = _minio.CreateClient();
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = MinioFixture.BucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        });
    }
}
