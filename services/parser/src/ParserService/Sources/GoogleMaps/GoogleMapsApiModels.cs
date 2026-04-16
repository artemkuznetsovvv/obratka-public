using System.Text.Json;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Parses Google Maps listugcposts API response (protobuf-over-JSON).
/// Response format: XSS prefix ")]}\'\n" + JSON array [null, nextPageToken, reviews[]].
/// </summary>
internal static class GoogleMapsResponseParser
{
    /// <summary>
    /// Strip the XSS protection prefix and parse the JSON array.
    /// </summary>
    public static JsonElement Parse(string responseText)
    {
        var json = responseText;
        if (json.StartsWith(")]}'"))
            json = json[(json.IndexOf('\n') + 1)..];

        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>
    /// Extract next page token from the parsed response.
    /// data[1] = base64 string or null.
    /// </summary>
    public static string? GetNextPageToken(JsonElement root)
    {
        if (root.GetArrayLength() < 2) return null;
        var tokenEl = root[1];
        return tokenEl.ValueKind == JsonValueKind.String ? tokenEl.GetString() : null;
    }

    /// <summary>
    /// Extract reviews from the parsed response.
    /// data[2] = array of review entries.
    /// </summary>
    public static IReadOnlyList<GoogleMapsReviewDto> GetReviews(JsonElement root)
    {
        if (root.GetArrayLength() < 3) return [];
        var reviewsArray = root[2];
        if (reviewsArray.ValueKind != JsonValueKind.Array) return [];

        var results = new List<GoogleMapsReviewDto>();
        foreach (var reviewWrapper in reviewsArray.EnumerateArray())
        {
            var dto = ParseSingleReview(reviewWrapper);
            if (dto != null)
                results.Add(dto);
        }

        return results;
    }

    /// <summary>
    /// Parse a single review entry.
    /// Structure: review[0] = [reviewId, metadata, content]
    /// </summary>
    private static GoogleMapsReviewDto? ParseSingleReview(JsonElement reviewWrapper)
    {
        try
        {
            if (reviewWrapper.ValueKind != JsonValueKind.Array || reviewWrapper.GetArrayLength() < 1)
                return null;

            var entry = reviewWrapper[0]; // [reviewId, metadata, content]
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 3)
                return null;

            var reviewId = GetString(entry, 0);
            if (reviewId == null) return null;

            var meta = entry[1]; // metadata array
            var content = entry[2]; // content array

            // Timestamp: meta[2] — microseconds since epoch
            long? timestampUs = GetLong(meta, 2);
            DateTimeOffset? date = timestampUs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestampUs.Value / 1000)
                : null;

            // Author: meta[4][5]
            string? authorName = null;
            string? authorId = null;
            if (TryGetElement(meta, 4, out var authorInfo) && TryGetElement(authorInfo, 5, out var authorDetail))
            {
                authorName = GetString(authorDetail, 0);
                authorId = GetString(authorDetail, 3);
            }

            // Rating: content[0][0]
            int? rating = null;
            if (TryGetElement(content, 0, out var ratingArr))
                rating = GetInt(ratingArr, 0);

            // Review text: content[15][0][0]
            string? text = null;
            if (TryGetElement(content, 15, out var textArr)
                && TryGetElement(textArr, 0, out var textInner))
            {
                text = GetString(textInner, 0);
            }

            // Language: content[14][0]
            string? language = null;
            if (TryGetElement(content, 14, out var langArr))
                language = GetString(langArr, 0);

            return new GoogleMapsReviewDto(
                ReviewId: reviewId,
                Text: text,
                Date: date,
                Rating: rating,
                AuthorName: authorName,
                AuthorId: authorId,
                Language: language);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() <= index)
            return null;
        var el = arr[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int? GetInt(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() <= index)
            return null;
        var el = arr[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    }

    private static long? GetLong(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() <= index)
            return null;
        var el = arr[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt64() : null;
    }

    private static bool TryGetElement(JsonElement arr, int index, out JsonElement result)
    {
        result = default;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() <= index)
            return false;
        result = arr[index];
        return result.ValueKind == JsonValueKind.Array;
    }
}

internal record GoogleMapsReviewDto(
    string ReviewId,
    string? Text,
    DateTimeOffset? Date,
    int? Rating,
    string? AuthorName,
    string? AuthorId,
    string? Language);
