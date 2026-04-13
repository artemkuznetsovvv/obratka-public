using System.Text.Json.Serialization;

namespace ParserService.Sources.TwoGis;

/// <summary>
/// Root response from GET /3.0/branches/{firmId}/reviews
/// </summary>
internal record TwoGisReviewsResponse(
    [property: JsonPropertyName("meta")] TwoGisMeta? Meta,
    [property: JsonPropertyName("reviews")] List<TwoGisReviewDto>? Reviews);

internal record TwoGisMeta(
    [property: JsonPropertyName("branch_rating")] double? BranchRating,
    [property: JsonPropertyName("branch_reviews_count")] int? BranchReviewsCount,
    [property: JsonPropertyName("total_count")] int? TotalCount,
    [property: JsonPropertyName("next_link")] string? NextLink);

internal record TwoGisReviewDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("rating")] int? Rating,
    [property: JsonPropertyName("date_created")] string? DateCreated,
    [property: JsonPropertyName("date_edited")] string? DateEdited,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("user")] TwoGisUserDto? User,
    [property: JsonPropertyName("is_hidden")] bool? IsHidden,
    [property: JsonPropertyName("official_answer")] TwoGisOfficialAnswerDto? OfficialAnswer);

internal record TwoGisUserDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("public_id")] string? PublicId,
    [property: JsonPropertyName("first_name")] string? FirstName);

internal record TwoGisOfficialAnswerDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("date_created")] string? DateCreated);
