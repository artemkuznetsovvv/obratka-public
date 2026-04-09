using System.Text.Json;
using FluentAssertions;
using ParserService.Sources.YandexMaps;

namespace ParserService.IntegrationTests.YandexMaps;

public class YandexApiModelsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialize_RealApiResponse_ParsesCorrectly()
    {
        var json = LoadFixture("fetchReviews_page1.json");

        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);

        root.Should().NotBeNull();
        root!.Data.Should().NotBeNull();
        root.Data!.Reviews.Should().HaveCount(3);
        root.Data.HasMore.Should().BeTrue();
        root.Data.TotalCount.Should().Be(5);
    }

    [Fact]
    public void Deserialize_ReviewFields_MappedCorrectly()
    {
        var json = LoadFixture("fetchReviews_page1.json");
        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);
        var review = root!.Data!.Reviews![0];

        review.ReviewId.Should().Be("rev-001");
        review.Text.Should().Be("Отличный сервис, всем рекомендую!");
        review.Rating.Should().Be(5);
        review.UpdatedTime.Should().Be("2025-04-01T12:00:00.000Z");
        review.TextLanguage.Should().Be("ru");
    }

    [Fact]
    public void Deserialize_AuthorFields_MappedCorrectly()
    {
        var json = LoadFixture("fetchReviews_page1.json");
        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);
        var author = root!.Data!.Reviews![0].Author;

        author.Should().NotBeNull();
        author!.Name.Should().Be("Иван Петров");
        author.PublicId.Should().Be("abc123def456");
    }

    [Fact]
    public void Deserialize_UpdatedTime_ParsesAsDateTimeOffset()
    {
        var json = LoadFixture("fetchReviews_page1.json");
        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);
        var review = root!.Data!.Reviews![0];

        DateTimeOffset.TryParse(review.UpdatedTime, out var date).Should().BeTrue();
        date.Year.Should().Be(2025);
        date.Month.Should().Be(4);
        date.Day.Should().Be(1);
    }

    [Fact]
    public void Deserialize_HasMoreFalse_ParsesCorrectly()
    {
        var json = LoadFixture("fetchReviews_page2.json");
        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);

        root!.Data!.HasMore.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_RealFormatSnippet_ParsesAllFields()
    {
        // Matches the exact format from real Yandex API response
        var json = """
        {
          "data": {
            "reviews": [
              {
                "businessComment": {
                  "text": "Спасибо за отзыв",
                  "updatedTime": "2025-02-19T07:12:06.979Z"
                },
                "reviewId": "CujRqrqcPXjSIhVHMyfYETjjYoNSUL",
                "businessId": "212641089354",
                "author": {
                  "name": "Валерия Дробинина",
                  "avatarUrl": "https://avatars.mds.yandex.net/get-yapic/47747/0l-1/{size}",
                  "professionLevel": "Знаток города 3 уровня",
                  "publicId": "5pct4h7wj2y3p4jkc2yhtwpbug"
                },
                "text": "Закончила обучение в автошколе",
                "textLanguage": "ru",
                "rating": 5,
                "updatedTime": "2025-02-16T15:49:17.071Z",
                "reactions": { "likes": 9, "dislikes": 0 },
                "photos": [],
                "videos": []
              }
            ],
            "totalCount": 342,
            "hasMore": true
          }
        }
        """;

        var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(json, JsonOptions);
        var review = root!.Data!.Reviews![0];

        review.ReviewId.Should().Be("CujRqrqcPXjSIhVHMyfYETjjYoNSUL");
        review.Rating.Should().Be(5);
        review.TextLanguage.Should().Be("ru");
        review.Author!.Name.Should().Be("Валерия Дробинина");
        review.Author.PublicId.Should().Be("5pct4h7wj2y3p4jkc2yhtwpbug");

        // Extra fields (businessComment, reactions, photos) should be silently ignored
        root.Data.TotalCount.Should().Be(342);
    }

    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "YandexMaps", "Fixtures", filename);
        return File.ReadAllText(path);
    }
}
