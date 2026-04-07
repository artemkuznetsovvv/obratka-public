using System.Collections.Concurrent;
using System.Text.Json;
using ParserService.Core.Models;
using ParserService.Infrastructure.Storage;

namespace ParserService.IntegrationTests.Helpers;

public class InMemoryS3ResultStorage : IS3ResultStorage
{
    public ConcurrentDictionary<string, string> Uploads { get; } = new();

    public Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct)
    {
        var key = $"{result.JobId}/raw/{result.Source}.json";
        var json = JsonSerializer.Serialize(result);
        Uploads[key] = json;
        return Task.FromResult($"s3://obratka-jobs/{key}");
    }
}
