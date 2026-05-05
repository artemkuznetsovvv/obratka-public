using System.Text.Json;
using ProcessingGateway.Application.Ingestion;

namespace ProcessingGateway.Tests.Infrastructure;

internal static class FixtureLoader
{
    /// `JsonNamingPolicy.SnakeCaseLower` — ровно как пишет Parser в `S3ResultStorage`.
    /// Доп. опции tolerant: `PropertyNameCaseInsensitive=true` чтобы случайный
    /// PascalCase не убил парсинг.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static CollectionResultPayload LoadRaw(string source)
    {
        // Файлы лежат в Fixtures/raw/{source}.json и копируются в bin как PreserveNewest.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "raw", $"{source}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found at {path}. Проверь <None Include=\"Fixtures\\**\\*.json\" CopyToOutputDirectory=\"PreserveNewest\" /> в csproj.",
                path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CollectionResultPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
    }
}
