using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace ParserService.Sources.YandexMaps;

internal sealed class YandexReviewApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger _logger;

    public YandexReviewApiClient(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<YandexReviewsResponse> FetchReviewsPageAsync(
        YandexSession session,
        string businessId,
        int page,
        int pageSize,
        string ranking,
        CancellationToken ct)
    {
        // Build params in alphabetical order (critical for hash computation)
        var queryParams = new List<KeyValuePair<string, string>>
        {
            new("ajax", "1"),
            new("businessId", businessId),
            new("csrfToken", session.CsrfToken),
            new("locale", session.Locale),
            new("page", page.ToString()),
            new("pageSize", pageSize.ToString()),
            new("ranking", ranking),
        };

        if (session.RequestId is not null)
            queryParams.Add(new("reqId", session.RequestId));

        queryParams.Add(new("sessionId", session.SessionId));

        var s = Djb2Hasher.ComputeS(queryParams);
        queryParams.Add(new("s", s));

        // Sort to ensure alphabetical order in URL
        queryParams.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        var queryString = string.Join("&",
            queryParams.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
        var url = $"{session.ApiBaseUrl}/maps/api/business/fetchReviews?{queryString}";

        _logger.LogDebug("Fetching reviews: businessId={BusinessId}, page={Page}, ranking={Ranking}",
            businessId, page, ranking);
        _logger.LogDebug("API URL: {Url}", url);

        var apiPage = await session.BrowserContext.NewPageAsync();
        try
        {
            await apiPage.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["Referer"] = $"{session.ApiBaseUrl}/maps/org/{businessId}/reviews/",
                ["Accept"] = "application/json",
                ["X-Requested-With"] = "XMLHttpRequest"
            });

            var response = await apiPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = 15_000
            });

            if (response == null)
                throw new HttpRequestException("No response from Yandex API");

            var status = response.Status;
            var body = await response.TextAsync();

            if (status != 200)
            {
                _logger.LogWarning("Yandex API returned status {Status}: {Body}", status, body[..Math.Min(body.Length, 500)]);
                throw new HttpRequestException($"Yandex API returned status {status}");
            }

            var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(body, JsonOptions);
            var result = root?.Data
                ?? throw new JsonException("Failed to deserialize Yandex reviews response");

            _logger.LogDebug("Got {Count} reviews on page {Page}", result.Reviews?.Count ?? 0, page);
            return result;
        }
        finally
        {
            await apiPage.CloseAsync();
        }
    }
}
