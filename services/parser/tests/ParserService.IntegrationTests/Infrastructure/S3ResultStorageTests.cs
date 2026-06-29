using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ParserService.Core.Models;
using ParserService.Infrastructure.Storage;
using ParserService.IntegrationTests.Fixtures;

namespace ParserService.IntegrationTests.Infrastructure;

[Collection("Docker")]
[Trait("Category", "Integration")]
public class S3ResultStorageTests : IAsyncLifetime
{
    private IAmazonS3 _s3 = null!;
    private S3ResultStorage _storage = null!;
    private const string BucketName = "obratka-jobs-test";

    public async Task InitializeAsync()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = "http://localhost:9000",
            ForcePathStyle = true
        };
        var credentials = new BasicAWSCredentials("minioadmin", "minioadmin");
        _s3 = new AmazonS3Client(credentials, config);

        try
        {
            await _s3.PutBucketAsync(BucketName);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou")
        {
            // Bucket already exists
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:BucketName"] = BucketName
            })
            .Build();

        _storage = new S3ResultStorage(_s3, configuration, NullLogger<S3ResultStorage>.Instance);
    }

    public async Task DisposeAsync()
    {
        try
        {
            var objects = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = BucketName
            });

            foreach (var obj in objects.S3Objects)
            {
                await _s3.DeleteObjectAsync(BucketName, obj.Key);
            }

            await _s3.DeleteBucketAsync(BucketName);
        }
        catch
        {
            // Cleanup best-effort
        }

        _s3.Dispose();
    }

    [Fact]
    public async Task UploadResultAsync_UploadsJsonToS3()
    {
        var result = new CollectionResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "2gis",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new List<RawReview>
            {
                new("ext-1", "Great place", DateTimeOffset.UtcNow, 5, Guid.NewGuid())
            });

        var s3Url = await _storage.UploadResultAsync(result, CancellationToken.None);

        s3Url.Should().StartWith($"s3://{BucketName}/");
        s3Url.Should().EndWith("/raw/2gis.json");

        // Verify object exists
        var key = $"{result.JobId}/raw/2gis.json";
        var obj = await _s3.GetObjectAsync(BucketName, key);
        obj.ContentLength.Should().BeGreaterThan(0);
    }
}
