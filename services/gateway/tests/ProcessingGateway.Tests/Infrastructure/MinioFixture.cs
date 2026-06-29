using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.Minio;

namespace ProcessingGateway.Tests.Infrastructure;

/// Один MinIO-контейнер на всю сборку. Bucket `obratka-jobs` создаётся при инициализации.
public class MinioFixture : IAsyncLifetime
{
    public const string BucketName = "obratka-jobs";

    public MinioContainer Container { get; } = new MinioBuilder("minio/minio:latest")
        .WithUsername("minioadmin")
        .WithPassword("minioadmin")
        .Build();

    public string Endpoint => $"http://{Container.Hostname}:{Container.GetMappedPublicPort(9000)}";
    public string AccessKey => "minioadmin";
    public string SecretKey => "minioadmin";

    public IAmazonS3 CreateClient() => new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
    {
        ServiceURL = Endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1"
    });

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition("Minio")]
public class MinioCollection : ICollectionFixture<MinioFixture> { }
