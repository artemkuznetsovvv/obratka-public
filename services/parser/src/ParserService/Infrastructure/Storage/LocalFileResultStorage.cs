using System.Text.Encodings.Web;
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
        WriteIndented = true,
        // Кириллица/эмодзи без \uXXXX-эскейпинга. См. комментарий в S3ResultStorage.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LocalFileResultStorage(IConfiguration configuration, ILogger<LocalFileResultStorage> logger)
    {
        _basePath = configuration["Storage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
        _logger = logger;
    }

    public async Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct)
    {
        var rawDir = Path.Combine(_basePath, result.JobId.ToString(), "raw");
        Directory.CreateDirectory(rawDir);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        // «Последний батч» — плоский файл (как раньше).
        var latestPath = Path.Combine(rawDir, $"{result.Source}.json");
        await File.WriteAllTextAsync(latestPath, json, ct);

        // Архив по циклам (вариант 2): raw/{source}/{taskId}.json — не перезатирается.
        var archiveDir = Path.Combine(rawDir, result.Source);
        Directory.CreateDirectory(archiveDir);
        var archivePath = Path.Combine(archiveDir, $"{result.TaskId}.json");
        await File.WriteAllTextAsync(archivePath, json, ct);

        _logger.LogInformation("Saved collection result to {Path} (archive: {ArchivePath})", latestPath, archivePath);
        return latestPath;
    }
}
