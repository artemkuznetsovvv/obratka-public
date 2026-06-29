using System.Text.Encodings.Web;
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
        WriteIndented = false,
        // По умолчанию System.Text.Json escape-ит non-ASCII (кириллица, эмодзи) в \uXXXX.
        // JSON валидный и десериализуется обратно корректно, но читать в S3 неприятно.
        // UnsafeRelaxedJsonEscaping выпускает UTF-8 как есть. «Unsafe» — про XSS при выводе
        // в HTML/JavaScript; для S3-файла, который читается JSON-парсером, безопасно.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public S3ResultStorage(IAmazonS3 s3, IConfiguration configuration, ILogger<S3ResultStorage> logger)
    {
        _s3 = s3;
        _bucketName = configuration["S3:BucketName"] ?? "obratka-jobs";
        _logger = logger;
    }

    public async Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);

        // Плоский ключ — «последний батч» источника: его читает ингест PG (по status.S3Url),
        // QA-скачивание `raw/<source>` и интеграционные тесты. Перезаписывается каждым сбором.
        var latestKey = $"{result.JobId}/raw/{result.Source}.json";
        // Архивный ключ — уникален на каждый сбор (taskId), поэтому НЕ перезатирается:
        // история сырья по циклам live-мониторинга сохраняется целиком (вариант 2).
        var archiveKey = $"{result.JobId}/raw/{result.Source}/{result.TaskId}.json";

        await PutAsync(latestKey, json, ct);
        await PutAsync(archiveKey, json, ct);

        var s3Url = $"s3://{_bucketName}/{latestKey}";
        _logger.LogInformation(
            "Uploaded collection result to {S3Url} (archive: {ArchiveKey})", s3Url, archiveKey);
        return s3Url;
    }

    private Task PutAsync(string key, string json, CancellationToken ct) =>
        _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            ContentBody = json,
            ContentType = "application/json"
        }, ct);
}
