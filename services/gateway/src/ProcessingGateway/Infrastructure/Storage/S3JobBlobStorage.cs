using System.Text.Encodings.Web;
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

    /// Для чтения output-файлов LLM и output-stub-ов: WhenWritingNull чтобы при сериализации
    /// (если когда-нибудь будем писать output из PG-стороны) лишние null-поля не плодились.
    /// Для чтения это поле не релевантно.
    private static readonly JsonSerializerOptions LlmJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// Для записи `input.json`: nullable-поля (`stars`, `text_language`) **всегда**
    /// сериализуются — даже как `null`. Иначе LLM-сервис интерпретирует отсутствие поля
    /// как «обязательное поле пропущено» и валидирует input как невалидный
    /// (см. LLM_PYTHON_QUICKSTART.md §3 — `stars: int?` обозначен как опциональный,
    /// но валидатор по дефолту проверяет наличие ключа). Решение — слать ключ всегда.
    private static readonly JsonSerializerOptions LlmInputJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
        // LlmInputJson (Never) — чтобы stars=null / text_language=null писались как ключи,
        // а не выпадали из JSON и не ломали валидатор LLM-сервиса.
        var json = JsonSerializer.Serialize(input, LlmInputJson);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        }, ct);

        _logger.LogInformation(
            "Uploaded LLM input for job {AnalysisJobId} to s3://{Bucket}/{Key} ({ReviewCount} reviews)",
            jobId, _bucketName, key, input.Reviews.Count);
    }

    public Task<LlmInput> ReadInputAsync(Guid jobId, CancellationToken ct = default)
        => ReadJsonAsync<LlmInput>(_bucketName, $"{jobId}/input.json", LlmJson, ct);

    public Task<LlmReviewsOutput> ReadReviewsOutputAsync(string s3Url, CancellationToken ct = default)
    {
        var (bucket, key) = S3UrlParser.Parse(s3Url);
        return ReadJsonAsync<LlmReviewsOutput>(bucket, key, LlmJson, ct);
    }

    public Task<LlmSummaryOutput> ReadSummaryOutputAsync(string s3Url, CancellationToken ct = default)
    {
        var (bucket, key) = S3UrlParser.Parse(s3Url);
        return ReadJsonAsync<LlmSummaryOutput>(bucket, key, LlmJson, ct);
    }

    public async Task WriteReviewsOutputAsync(Guid jobId, LlmReviewsOutput output, CancellationToken ct = default)
    {
        var key = $"{jobId}/output_reviews.json";
        var json = JsonSerializer.Serialize(output, LlmJson);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        }, ct);

        _logger.LogInformation(
            "Uploaded LLM reviews-output for job {AnalysisJobId} to s3://{Bucket}/{Key} ({Count} reviews)",
            jobId, _bucketName, key, output.Reviews.Count);
    }

    public async Task WriteSummaryOutputAsync(Guid jobId, LlmSummaryOutput output, CancellationToken ct = default)
    {
        var key = $"{jobId}/output_summary.json";
        var json = JsonSerializer.Serialize(output, LlmJson);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        }, ct);

        _logger.LogInformation(
            "Uploaded LLM summary-output for job {AnalysisJobId} to s3://{Bucket}/{Key} ({Recs} recommendations)",
            jobId, _bucketName, key, output.RecommendationsCount);
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
