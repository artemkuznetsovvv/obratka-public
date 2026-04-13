using System.Text.Json;

namespace ParserService.Sources.TwoGis;

/// <summary>
/// HTTP-клиент для public-api.reviews.2gis.com/3.0.
/// Не требует браузера, cookies или сессии — только API-ключ.
/// </summary>
internal sealed class TwoGisApiClient
{
    private const string BaseUrl = "https://public-api.reviews.2gis.com/3.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public TwoGisApiClient(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Запрос страницы отзывов через публичный API 2GIS.
    /// </summary>
    public async Task<TwoGisReviewsResponse> FetchReviewsPageAsync(
        string firmId,
        string apiKey,
        int limit,
        int offset,
        string sortBy,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/branches/{firmId}/reviews"
            + $"?limit={limit}&offset={offset}"
            + $"&is_advertiser=false"
            + $"&fields=meta.providers,meta.branch_rating,meta.branch_reviews_count,meta.total_count"
            + $"&sort_by={sortBy}"
            + $"&key={apiKey}"
            + $"&locale=ru_RU";

        _logger.LogDebug("[2GIS-ApiClient] GET отзывы: firmId={FirmId}, offset={Offset}, limit={Limit}, sort={Sort}",
            firmId, offset, limit, sortBy);

        var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("[2GIS-ApiClient] Ответ: статус={Status}, размер={Length} байт",
            (int)response.StatusCode, body.Length);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[2GIS-ApiClient] Ошибка API: {Status}. Тело: {Body}",
                (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
            throw new HttpRequestException($"2GIS API returned {(int)response.StatusCode}");
        }

        var result = JsonSerializer.Deserialize<TwoGisReviewsResponse>(body, JsonOptions);
        if (result == null)
        {
            _logger.LogError("[2GIS-ApiClient] Не удалось десериализовать ответ: {Body}",
                body.Length > 500 ? body[..500] : body);
            throw new JsonException("Failed to deserialize 2GIS reviews response");
        }

        _logger.LogDebug("[2GIS-ApiClient] Получено {Count} отзывов, total={Total}, hasNext={HasNext}",
            result.Reviews?.Count ?? 0,
            result.Meta?.TotalCount,
            result.Meta?.NextLink != null);

        return result;
    }
}
