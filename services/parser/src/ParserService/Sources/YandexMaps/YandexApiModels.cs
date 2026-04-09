using System.Text.Json.Serialization;

namespace ParserService.Sources.YandexMaps;

/// <summary>
/// Top-level wrapper — real API response is { "data": { "reviews": [...] } }
/// </summary>
internal record YandexFetchReviewsRoot(
    [property: JsonPropertyName("data")] YandexReviewsResponse? Data);

internal record YandexReviewsResponse(
    [property: JsonPropertyName("reviews")] List<YandexReviewDto>? Reviews,
    [property: JsonPropertyName("totalCount")] int? TotalCount,
    [property: JsonPropertyName("hasMore")] bool? HasMore);

internal record YandexReviewDto(
    [property: JsonPropertyName("reviewId")] string? ReviewId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("updatedTime")] string? UpdatedTime,
    [property: JsonPropertyName("rating")] int? Rating,
    [property: JsonPropertyName("author")] YandexAuthorDto? Author);

internal record YandexAuthorDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("publicId")] string? PublicId);
