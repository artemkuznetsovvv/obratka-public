using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParserService.Core.Models;

public enum SourceType
{
    TwoGis,
    YandexMaps,
    GoogleMaps,
    Otzovik
}

public static class SourceTypeExtensions
{
    private static readonly Dictionary<SourceType, string> ToSlugMap = new()
    {
        [SourceType.TwoGis] = "2gis",
        [SourceType.YandexMaps] = "yandex",
        [SourceType.GoogleMaps] = "google",
        [SourceType.Otzovik] = "otzovik"
    };

    private static readonly Dictionary<string, SourceType> FromSlugMap =
        ToSlugMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string ToSlug(this SourceType source) => ToSlugMap[source];

    public static SourceType FromSlug(string slug) =>
        FromSlugMap.TryGetValue(slug, out var source)
            ? source
            : throw new ArgumentException($"Unknown source slug: '{slug}'", nameof(slug));

    public static bool TryFromSlug(string slug, out SourceType source) =>
        FromSlugMap.TryGetValue(slug, out source);
}

public class SourceTypeJsonConverter : JsonConverter<SourceType>
{
    public override SourceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var slug = reader.GetString() ?? throw new JsonException("Expected non-null source type string");
        return SourceTypeExtensions.FromSlug(slug);
    }

    public override void Write(Utf8JsonWriter writer, SourceType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToSlug());
    }
}
