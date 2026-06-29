using System.Text.Json;

namespace ParserService.Sources.GoogleMaps;

/// <summary>
/// Parses Google Maps reviews API response (protobuf-over-JSON).
/// Current endpoint (май 2026): POST /maps/_/MapsWizUi/data/batchexecute?rpcids=...
/// — ответ упакован в batchexecute streaming wrapper:
///   ")]}\'\n<len>\n<json-frames>\n<len>\n<json-frames>\n..."
/// каждый frame = ["wrb.fr","/MapsUgcPostService.ListUgcPosts","<inner-json-string>",null,...]
/// inner-json (после второго парса) = [null, nextPageToken, reviews[]] — старый формат,
/// поэтому ParseSingleReview переиспользуется как есть.
/// </summary>
internal static class GoogleMapsResponseParser
{
    private const string ListUgcPostsRpcName = "/MapsUgcPostService.ListUgcPosts";

    /// <summary>
    /// Strip the XSS protection prefix and parse the JSON array.
    /// Используется только тестами и legacy кодом (старый listugcposts endpoint).
    /// Для batchexecute берите ExtractListUgcPostsFrames + GetReviews.
    /// </summary>
    public static JsonElement Parse(string responseText)
    {
        var json = responseText;
        if (json.StartsWith(")]}'"))
            json = json[(json.IndexOf('\n') + 1)..];

        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>
    /// Распаковывает batchexecute streaming-ответ и возвращает все вложенные
    /// JsonElement-корни для фреймов с RPC = "/MapsUgcPostService.ListUgcPosts".
    /// На один HTTP-ответ обычно один такой фрейм, но Google может батчить несколько
    /// RPC в один POST — поэтому пробегаем все.
    ///
    /// Поток: `)]}\'\n` (XSS) + повторяющееся `<length-decimal>\n<chunk-as-json>`.
    /// **Length-prefix НЕ используется** — Google считает его как-то по-своему (для нашего
    /// chunk-а 27317 при реальном JSON char-length 27315, byte-length 28530), и любой
    /// надёжной формулы я не нашёл. Вместо этого находим конец chunk-а по балансу скобок
    /// — безопасно в UTF-8, потому что ASCII-символы `[`, `]`, `"`, `\` никогда не
    /// встречаются как байт-в-середине multi-byte sequence.
    /// </summary>
    public static IReadOnlyList<JsonElement> ExtractListUgcPostsFrames(byte[] body)
    {
        var results = new List<JsonElement>();
        if (body == null || body.Length == 0) return results;

        int idx = 0;

        // 1. XSS prefix `)]}'` (опционально) и далее ближайший '\n'
        if (body.Length >= 4 && body[0] == (byte)')' && body[1] == (byte)']'
            && body[2] == (byte)'}' && body[3] == (byte)'\'')
        {
            idx = 4;
            while (idx < body.Length && body[idx] != (byte)'\n') idx++;
            if (idx < body.Length) idx++;
        }

        while (idx < body.Length)
        {
            // skip whitespace перед длиной
            while (idx < body.Length && IsAsciiWhitespace(body[idx])) idx++;
            if (idx >= body.Length) break;

            // Пропускаем декларированную длину (не используем — см. XML-doc)
            while (idx < body.Length && body[idx] != (byte)'\n') idx++;
            if (idx >= body.Length) break;
            idx++; // \n

            // Дальше должен быть chunk JSON — найдём его начало (первый `[` или `{`)
            while (idx < body.Length && IsAsciiWhitespace(body[idx])) idx++;
            if (idx >= body.Length) break;
            if (body[idx] != (byte)'[' && body[idx] != (byte)'{') break;

            int chunkStart = idx;
            int chunkEnd = ScanJsonEnd(body, idx);
            if (chunkEnd <= chunkStart) break;
            idx = chunkEnd;

            var chunkMem = new ReadOnlyMemory<byte>(body, chunkStart, chunkEnd - chunkStart);

            JsonElement chunkRoot;
            try
            {
                using var doc = JsonDocument.Parse(chunkMem);
                chunkRoot = doc.RootElement.Clone();
            }
            catch { continue; }

            if (chunkRoot.ValueKind != JsonValueKind.Array) continue;

            foreach (var frame in chunkRoot.EnumerateArray())
            {
                if (frame.ValueKind != JsonValueKind.Array || frame.GetArrayLength() < 3) continue;
                if (frame[0].ValueKind != JsonValueKind.String || frame[0].GetString() != "wrb.fr") continue;
                if (frame[1].ValueKind != JsonValueKind.String || frame[1].GetString() != ListUgcPostsRpcName) continue;
                if (frame[2].ValueKind != JsonValueKind.String) continue;

                var inner = frame[2].GetString();
                if (string.IsNullOrEmpty(inner)) continue;

                JsonElement innerRoot;
                try
                {
                    using var innerDoc = JsonDocument.Parse(inner);
                    innerRoot = innerDoc.RootElement.Clone();
                }
                catch { continue; }

                results.Add(innerRoot);
            }
        }

        return results;
    }

    /// <summary>
    /// Возвращает индекс байта, СЛЕДУЮЩИЙ за концом сбалансированного JSON-объекта,
    /// который начинается в <paramref name="start"/> (там должно быть `[` или `{`).
    /// Уважает string-литералы и `\`-escapes. Бросает 0 если не нашли — лучше тихо завершить парсинг.
    /// </summary>
    private static int ScanJsonEnd(byte[] body, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < body.Length; i++)
        {
            byte b = body[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (b == (byte)'\\') { escape = true; continue; }
                if (b == (byte)'"') inString = false;
                continue;
            }
            if (b == (byte)'"') { inString = true; continue; }
            if (b == (byte)'[' || b == (byte)'{') depth++;
            else if (b == (byte)']' || b == (byte)'}')
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return 0;
    }

    private static bool IsAsciiWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t';

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
