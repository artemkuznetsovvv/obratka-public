using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace ParserService.Sources.YandexMaps;

internal sealed class YandexReviewApiClient
{
    private const string BaseUrl = "https://yandex.ru/maps/api/business/fetchReviews";
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
        var queryParams = new OrderedDictionary<string, string>
        {
            ["businessId"] = businessId,
            ["csrfToken"] = session.CsrfToken,
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString(),
            ["ranking"] = ranking,
            ["sessionId"] = session.SessionId
        };

        var s = Djb2Hasher.ComputeS(queryParams);
        queryParams["s"] = s;

        var queryString = string.Join("&",
            queryParams.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
        var url = $"{BaseUrl}?{queryString}";

        _logger.LogDebug("Fetching reviews: businessId={BusinessId}, page={Page}, ranking={Ranking}",
            businessId, page, ranking);

        var apiPage = await session.BrowserContext.NewPageAsync();
        try
        {
            await apiPage.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["Referer"] = $"https://yandex.ru/maps/org/{businessId}/reviews/",
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

            var result = JsonSerializer.Deserialize<YandexReviewsResponse>(body, JsonOptions)
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

internal sealed class OrderedDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly List<TKey> _keys = [];

    public new TValue this[TKey key]
    {
        get => base[key];
        set
        {
            if (!ContainsKey(key))
                _keys.Add(key);
            base[key] = value;
        }
    }

    public new void Add(TKey key, TValue value)
    {
        base.Add(key, value);
        _keys.Add(key);
    }

    public new IEnumerable<TValue> Values => _keys.Select(k => base[k]);
}
