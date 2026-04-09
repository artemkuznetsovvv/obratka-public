using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParserService.Core.Models;
using ParserService.Sources.YandexMaps;

namespace ParserService.IntegrationTests.YandexMaps;

public class YandexReviewCollectorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly YandexMapsOptions _options = new()
    {
        PageSize = 50,
        MaxPages = 12,
        DelayBetweenPagesMinMs = 0,
        DelayBetweenPagesMaxMs = 0,
    };

    private readonly BranchTarget _branch = new(
        BranchId: Guid.NewGuid(),
        ExternalId: "212641089354",
        ExternalUrl: "https://yandex.ru/maps/org/212641089354/");

    /// <summary>
    /// Full collection (no date_from) should use dual sorting and deduplicate.
    /// Page 2 contains a duplicate (rev-002) — it must appear only once.
    /// </summary>
    [Fact]
    public async Task CollectAllReviews_FullCollection_DeduplicatesAcrossPages()
    {
        var fakeClient = new FakeYandexReviewApiClient(new Dictionary<(string ranking, int page), string>
        {
            [("by_time", 1)] = LoadFixture("fetchReviews_page1.json"),
            [("by_time", 2)] = LoadFixture("fetchReviews_page2.json"),
            [("by_relevance_org", 1)] = LoadFixture("fetchReviews_page1.json"),
            [("by_relevance_org", 2)] = LoadFixture("fetchReviews_page2.json"),
        });

        var collector = new YandexReviewCollector(fakeClient, _options, NullLogger.Instance);

        var period = new DateRange(DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        var reviews = await collector.CollectAllReviewsAsync(
            null!, _branch, period, CancellationToken.None);

        // 4 unique review IDs across both pages (rev-002 is a duplicate)
        reviews.Should().HaveCount(4);
        reviews.Select(r => r.ExternalId).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Incremental collection (date_from set) should stop when it reaches a review
    /// older than the date boundary.
    /// </summary>
    [Fact]
    public async Task CollectAllReviews_Incremental_StopsAtDateBoundary()
    {
        var fakeClient = new FakeYandexReviewApiClient(new Dictionary<(string ranking, int page), string>
        {
            [("by_time", 1)] = LoadFixture("fetchReviews_page1.json"),
            [("by_time", 2)] = LoadFixture("fetchReviews_page2.json"),
        });

        var collector = new YandexReviewCollector(fakeClient, _options, NullLogger.Instance);

        // Only reviews from March 2025 onwards
        var period = new DateRange(
            new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var reviews = await collector.CollectAllReviewsAsync(
            null!, _branch, period, CancellationToken.None);

        // rev-001 (Apr 1), rev-002 (Mar 28), rev-003 (Mar 15) are in range
        // rev-004 (Feb 20) is before March 1 — excluded, triggers stop
        reviews.Should().HaveCount(3);
        reviews.Should().OnlyContain(r => r.Date >= period.From);
    }

    /// <summary>
    /// Author fields and TextLanguage should be mapped from the API response.
    /// </summary>
    [Fact]
    public async Task CollectAllReviews_MapsAuthorAndLanguageFields()
    {
        var fakeClient = new FakeYandexReviewApiClient(new Dictionary<(string ranking, int page), string>
        {
            [("by_time", 1)] = LoadFixture("fetchReviews_page1.json"),
        });

        // Use incremental to avoid dual-sort complexity
        var collector = new YandexReviewCollector(fakeClient, _options, NullLogger.Instance);
        var period = new DateRange(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var reviews = await collector.CollectAllReviewsAsync(
            null!, _branch, period, CancellationToken.None);

        var first = reviews.First(r => r.ExternalId == "rev-001");
        first.AuthorName.Should().Be("Иван Петров");
        first.AuthorPublicId.Should().Be("abc123def456");
        first.TextLanguage.Should().Be("ru");

        var english = reviews.First(r => r.ExternalId == "rev-003");
        english.TextLanguage.Should().Be("en");
        english.AuthorName.Should().Be("John Smith");
    }

    /// <summary>
    /// BranchId from BranchTarget should be propagated to every RawReview.
    /// </summary>
    [Fact]
    public async Task CollectAllReviews_SetsBranchIdOnAllReviews()
    {
        var fakeClient = new FakeYandexReviewApiClient(new Dictionary<(string ranking, int page), string>
        {
            [("by_time", 1)] = LoadFixture("fetchReviews_page1.json"),
        });

        var collector = new YandexReviewCollector(fakeClient, _options, NullLogger.Instance);
        var period = new DateRange(
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var reviews = await collector.CollectAllReviewsAsync(
            null!, _branch, period, CancellationToken.None);

        reviews.Should().OnlyContain(r => r.BranchId == _branch.BranchId);
    }

    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "YandexMaps", "Fixtures", filename);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Fake API client that returns pre-recorded JSON responses by (ranking, page).
    /// </summary>
    private class FakeYandexReviewApiClient : YandexReviewApiClient
    {
        private readonly Dictionary<(string ranking, int page), YandexReviewsResponse> _responses;

        public FakeYandexReviewApiClient(
            Dictionary<(string ranking, int page), string> fixtures)
            : base(NullLogger.Instance)
        {
            _responses = fixtures.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(
                        kv.Value, JsonOptions);
                    return root?.Data ?? new YandexReviewsResponse(null, null, false);
                });
        }

        public override Task<YandexReviewsResponse> FetchReviewsPageAsync(
            YandexSession session,
            string businessId,
            int page,
            int pageSize,
            string ranking,
            CancellationToken ct)
        {
            if (_responses.TryGetValue((ranking, page), out var response))
                return Task.FromResult(response);

            return Task.FromResult(new YandexReviewsResponse(null, null, false));
        }
    }
}
