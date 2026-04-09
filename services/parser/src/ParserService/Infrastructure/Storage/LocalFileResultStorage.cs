using System.Text.Json;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.Storage;

public class LocalFileResultStorage : IS3ResultStorage
{
    private readonly string _basePath;
    private readonly ILogger<LocalFileResultStorage> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public LocalFileResultStorage(IConfiguration configuration, ILogger<LocalFileResultStorage> logger)
    {
        _basePath = configuration["Storage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
        _logger = logger;
    }

    public async Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct)
    {
        var dir = Path.Combine(_basePath, result.JobId.ToString(), "raw");
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, $"{result.Source}.json");
        var json = JsonSerializer.Serialize(result, JsonOptions);

        await File.WriteAllTextAsync(filePath, json, ct);

        _logger.LogInformation("Saved collection result to {Path}", filePath);
        return filePath;
    }
}
