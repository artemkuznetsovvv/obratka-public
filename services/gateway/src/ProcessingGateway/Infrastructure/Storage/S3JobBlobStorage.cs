using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ProcessingGateway.Application.Ingestion;
using ProcessingGateway.Application.Llm;

namespace ProcessingGateway.Infrastructure.Storage;

public sealed class S3JobBlobStorage : IJobBlobStorage
{
    private static readonly JsonSerializerOptions ParserPayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    /// LlmInput / LlmOutput используют [JsonPropertyName(...)] атрибуты — naming policy не нужен.
    private static readonly JsonSerializerOptions LlmJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;
    private readonly ILogger<S3JobBlobStorage> _logger;

    public S3JobBlobStorage(IAmazonS3 s3, IConfiguration configuration, ILogger<S3JobBlobStorage> logger)
    {
        _s3 = s3;
        _bucketName = configuration["S3:BucketName"]
            ?? throw new InvalidOperationException("S3:BucketName is not configured");
        _logger = logger;
    }

    public Task<CollectionResultPayload> ReadRawAsync(Guid jobId, string sourceSlug, CancellationToken ct = default)
        => ReadJsonAsync<CollectionResultPayload>(_bucketName, $"{jobId}/raw/{sourceSlug}.json", ParserPayloadJson, ct);

    public Task<CollectionResultPayload> ReadRawAsync(string s3Url, CancellationToken ct = default)
    {
        var (bucket, key) = S3UrlParser.Parse(s3Url);
        return ReadJsonAsync<CollectionResultPayload>(bucket, key, ParserPayloadJson, ct);
    }

    public async Task WriteInputAsync(Guid jobId, LlmInput input, CancellationToken ct = default)
    {
        var key = $"{jobId}/input.json";
        var json = JsonSerializer.Serialize(input, LlmJson);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        };

        await _s3.PutObjectAsync(request, ct);
        _logger.LogInformation(
            "Uploaded LLM input for job {AnalysisJobId} to s3://{Bucket}/{Key} ({ReviewCount} reviews)",
            jobId, _bucketName, key, input.Reviews.Count);
    }

    public Task<LlmOutput> ReadOutputAsync(Guid jobId, CancellationToken ct = default)
        => ReadJsonAsync<LlmOutput>(_bucketName, $"{jobId}/output.json", LlmJson, ct);

    public Task<LlmInput> ReadInputAsync(Guid jobId, CancellationToken ct = default)
        => ReadJsonAsync<LlmInput>(_bucketName, $"{jobId}/input.json", LlmJson, ct);

    public async Task WriteOutputAsync(Guid jobId, LlmOutput output, CancellationToken ct = default)
    {
        var key = $"{jobId}/output.json";
        var json = JsonSerializer.Serialize(output, LlmJson);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        }, ct);

        _logger.LogInformation(
            "Uploaded LLM output for job {AnalysisJobId} to s3://{Bucket}/{Key} ({ProcessedCount} processed)",
            jobId, _bucketName, key, output.ProcessedReview.Count);
    }

    private async Task<T> ReadJsonAsync<T>(string bucket, string key, JsonSerializerOptions options, CancellationToken ct)
    {
        using var response = await _s3.GetObjectAsync(bucket, key, ct);
        using var stream = response.ResponseStream;

        var result = await JsonSerializer.DeserializeAsync<T>(stream, options, ct)
            ?? throw new InvalidOperationException($"Empty body at s3://{bucket}/{key}");

        return result;
    }
}
