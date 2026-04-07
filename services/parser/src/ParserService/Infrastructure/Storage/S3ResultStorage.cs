using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Storage;

public class S3ResultStorage : IS3ResultStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;
    private readonly ILogger<S3ResultStorage> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public S3ResultStorage(IAmazonS3 s3, IConfiguration configuration, ILogger<S3ResultStorage> logger)
    {
        _s3 = s3;
        _bucketName = configuration["S3:BucketName"] ?? "obratka-jobs";
        _logger = logger;
    }

    public async Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct)
    {
        var key = $"{result.JobId}/raw/{result.Source}.json";
        var json = JsonSerializer.Serialize(result, JsonOptions);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        };

        await _s3.PutObjectAsync(request, ct);

        var s3Url = $"s3://{_bucketName}/{key}";
        _logger.LogInformation("Uploaded collection result to {S3Url}", s3Url);
        return s3Url;
    }
}
