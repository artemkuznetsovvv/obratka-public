using System.Text.Json.Serialization;

namespace ParserService.Sources.YandexMaps;

internal record YandexReviewsResponse(
    [property: JsonPropertyName("reviews")] List<YandexReviewDto>? Reviews,
    [property: JsonPropertyName("totalCount")] int? TotalCount,
    [property: JsonPropertyName("hasMore")] bool? HasMore);

internal record YandexReviewDto(
    [property: JsonPropertyName("reviewId")] string? ReviewId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("updatedTime")] long? UpdatedTime,
    [property: JsonPropertyName("rating")] int? Rating,
    [property: JsonPropertyName("author")] YandexAuthorDto? Author);

internal record YandexAuthorDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("publicId")] string? PublicId);
