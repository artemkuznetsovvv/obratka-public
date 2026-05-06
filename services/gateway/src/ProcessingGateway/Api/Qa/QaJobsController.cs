using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;

namespace ProcessingGateway.Api.Qa;

/// QA-эндпоинты для S3-блобов конкретного job-а: listing + raw-content
/// (raw/{source}.json, input.json, output_reviews.json, output_summary.json).
/// Удобно для отладки — одной командой получить картину «что есть в хранилище».
[ApiController]
[Route("api/qa/jobs")]
[RequireQaApiKey]
public sealed class QaJobsController : ControllerBase
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public QaJobsController(IAmazonS3 s3, IConfiguration configuration)
    {
        _s3 = s3;
        _bucket = configuration["S3:BucketName"]
            ?? throw new InvalidOperationException("S3:BucketName is not configured");
    }

    /// Листинг всех S3-ключей префикса `{jobId}/` с size + last_modified.
    [HttpGet("{jobId:guid}/blobs")]
    public async Task<IActionResult> ListBlobs(Guid jobId, CancellationToken ct)
    {
        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = $"{jobId}/"
        }, ct);

        var items = response.S3Objects?
            .Select(o => new
            {
                key = o.Key,
                size = o.Size,
                last_modified = o.LastModified
            }).ToList() ?? new();

        return Ok(new { bucket = _bucket, prefix = $"{jobId}/", count = items.Count, items });
    }

    /// Тело конкретного блоба. Поток без буферизации — для произвольного размера.
    /// Белый список: raw/{source}, input, output_reviews, output_summary
    /// (а также короткое `output` как алиас на output_reviews для совместимости).
    /// `{*name}` — catch-all, чтобы пропустить слэш в `raw/yandex`.
    [HttpGet("{jobId:guid}/blobs/{*name}")]
    public async Task<IActionResult> GetBlob(Guid jobId, string name, CancellationToken ct)
    {
        var key = ResolveKey(jobId, name);
        if (key is null) return BadRequest(new
        {
            error = $"Unknown blob name '{name}'. Allowed: input, output_reviews, output_summary, raw/<source>"
        });

        try
        {
            var resp = await _s3.GetObjectAsync(_bucket, key, ct);
            var ms = new MemoryStream();
            await resp.ResponseStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return File(ms, resp.Headers.ContentType ?? "application/json", $"{jobId}-{name}.json");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = $"s3://{_bucket}/{key} not found" });
        }
    }

    /// `input` / `output_reviews` / `output_summary` / `raw/yandex` (со слэшем).
    private static string? ResolveKey(Guid jobId, string name) => name.ToLowerInvariant() switch
    {
        "input" => $"{jobId}/input.json",
        "output_reviews" => $"{jobId}/output_reviews.json",
        "output_summary" => $"{jobId}/output_summary.json",
        var s when s.StartsWith("raw/") =>
            // raw/yandex → {jobId}/raw/yandex.json
            $"{jobId}/{s}.json",
        _ => null
    };
}
