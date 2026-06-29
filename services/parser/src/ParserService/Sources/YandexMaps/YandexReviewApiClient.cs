using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace ParserService.Sources.YandexMaps;

internal class YandexReviewApiClient
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

    public virtual async Task<YandexReviewsResponse> FetchReviewsPageAsync(
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

        _logger.LogDebug("[ApiClient] Запрос отзывов: businessId={BusinessId}, стр.{Page}, сортировка={Ranking}, pageSize={PageSize}",
            businessId, page, ranking, pageSize);
        _logger.LogDebug("[ApiClient] URL: {Url}", url);

        var apiPage = await session.BrowserContext.NewPageAsync();
        try
        {
            await apiPage.SetExtraHTTPHeadersAsync(new Dictionary<string, string>
            {
                ["Referer"] = $"{session.ApiBaseUrl}/maps/org/{businessId}/reviews/",
                ["Accept"] = "application/json",
                ["X-Requested-With"] = "XMLHttpRequest"
            });

            _logger.LogDebug("[ApiClient] Отправляю запрос (таймаут: 15с)...");
            var response = await apiPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = 15_000
            });

            if (response == null)
            {
                _logger.LogError("[ApiClient] Ответ от Яндекс API = null");
                throw new HttpRequestException("No response from Yandex API");
            }

            var status = response.Status;
            var body = await response.TextAsync();

            _logger.LogDebug("[ApiClient] Ответ: статус={Status}, размер={Length} байт", status, body.Length);

            if (status != 200)
            {
                _logger.LogWarning("[ApiClient] Ошибка API, статус {Status}. Тело ответа: {Body}",
                    status, body.Length > 500 ? body[..500] : body);
                throw new HttpRequestException($"Yandex API returned status {status}");
            }

            var root = JsonSerializer.Deserialize<YandexFetchReviewsRoot>(body, JsonOptions);
            var result = root?.Data;

            if (result == null)
            {
                _logger.LogError("[ApiClient] Не удалось десериализовать ответ. Тело (первые 500 симв.): {Body}",
                    body.Length > 500 ? body[..500] : body);
                throw new JsonException("Failed to deserialize Yandex reviews response");
            }

            _logger.LogDebug("[ApiClient] Стр.{Page}: {Count} отзывов, hasMore={HasMore}",
                page, result.Reviews?.Count ?? 0, result.HasMore);
            return result;
        }
        finally
        {
            await apiPage.CloseAsync();
        }
    }
}
