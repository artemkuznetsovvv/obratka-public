using ParserService.Core.Models;

namespace ParserService.Sources.YandexMaps;

internal sealed class YandexReviewCollector
{
    private readonly YandexReviewApiClient _apiClient;
    private readonly YandexMapsOptions _options;
    private readonly ILogger _logger;

    public YandexReviewCollector(
        YandexReviewApiClient apiClient,
        YandexMapsOptions options,
        ILogger logger)
    {
        _apiClient = apiClient;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawReview>> CollectAllReviewsAsync(
        YandexSession session,
        BranchTarget branch,
        DateRange period,
        CancellationToken ct)
    {
        var isIncremental = period.From != DateTimeOffset.MinValue;

        if (isIncremental)
        {
            _logger.LogInformation("Incremental collection for {BusinessId} from {From}",
                branch.ExternalId, period.From);
            return await CollectWithSortingAsync(session, branch, period, "by_time", stopOnDate: true, ct);
        }

        _logger.LogInformation("Full collection for {BusinessId} with dual sorting", branch.ExternalId);

        var byTime = await CollectWithSortingAsync(session, branch, period, "by_time", stopOnDate: false, ct);
        var byRelevance = await CollectWithSortingAsync(session, branch, period, "by_relevance_org", stopOnDate: false, ct);

        var deduplicated = DeduplicateReviews(byTime, byRelevance);

        _logger.LogInformation("Collected {Total} unique reviews ({ByTime} by_time + {ByRelevance} by_relevance, {Dupes} duplicates removed)",
            deduplicated.Count, byTime.Count, byRelevance.Count, byTime.Count + byRelevance.Count - deduplicated.Count);

        return deduplicated;
    }

    private async Task<IReadOnlyList<RawReview>> CollectWithSortingAsync(
        YandexSession session,
        BranchTarget branch,
        DateRange period,
        string ranking,
        bool stopOnDate,
        CancellationToken ct)
    {
        var reviews = new List<RawReview>();

        for (int page = 1; page <= _options.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await _apiClient.FetchReviewsPageAsync(
                session, branch.ExternalId, page, _options.PageSize, ranking, ct);

            if (response.Reviews == null || response.Reviews.Count == 0)
                break;

            bool reachedDateBound = false;

            foreach (var dto in response.Reviews)
            {
                if (dto.ReviewId == null || dto.Rating == null || dto.UpdatedTime == null)
                    continue;

                var date = DateTimeOffset.FromUnixTimeSeconds(dto.UpdatedTime.Value);

                if (date < period.From)
                {
                    if (stopOnDate)
                    {
                        reachedDateBound = true;
                        break;
                    }
                    continue;
                }

                if (date > period.To)
                    continue;

                reviews.Add(new RawReview(
                    ExternalId: dto.ReviewId,
                    Text: dto.Text ?? "",
                    Date: date,
                    Stars: dto.Rating.Value,
                    BranchId: branch.BranchId));
            }

            if (reachedDateBound)
            {
                _logger.LogDebug("Reached date boundary on page {Page} for ranking {Ranking}", page, ranking);
                break;
            }

            if (response.HasMore != true)
                break;

            // Delay between pages
            var delay = Random.Shared.Next(_options.DelayBetweenPagesMinMs, _options.DelayBetweenPagesMaxMs + 1);
            await Task.Delay(delay, ct);
        }

        _logger.LogDebug("Collected {Count} reviews with ranking {Ranking}", reviews.Count, ranking);
        return reviews;
    }

    private static List<RawReview> DeduplicateReviews(
        IReadOnlyList<RawReview> first,
        IReadOnlyList<RawReview> second)
    {
        var seen = new HashSet<string>();
        var result = new List<RawReview>();

        foreach (var review in first.Concat(second))
        {
            if (seen.Add(review.ExternalId))
                result.Add(review);
        }

        return result;
    }
}
