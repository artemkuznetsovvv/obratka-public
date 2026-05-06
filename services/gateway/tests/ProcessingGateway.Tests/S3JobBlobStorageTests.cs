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

    [Fact(Skip = "schema_2_0_migration: rewrite for output_reviews+output_summary round-trip after demo")]
    public Task WriteInput_then_ReadOutput_full_llm_round_trip() => Task.CompletedTask;

    [Fact(Skip = "schema_2_0_migration: processedReview camelCase удалён; новые тесты под output_reviews/output_summary")]
    public Task ReadOutput_correctly_handles_processedReview_camelCase_field() => Task.CompletedTask;

    private async Task UploadFixtureAsync(Guid jobId, string source)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "raw", $"{source}.json");
        var json = await File.ReadAllTextAsync(fixturePath);

        await UploadRawJsonAsync($"{jobId}/raw/{source}.json", json);
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
