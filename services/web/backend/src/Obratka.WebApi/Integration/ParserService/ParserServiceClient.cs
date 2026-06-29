using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Obratka.WebApi.Integration.ParserService.Contracts;

namespace Obratka.WebApi.Integration.ParserService;

internal sealed class ParserServiceClient(HttpClient httpClient) : IParserServiceClient
{
    // Parser-Service serializes JSON with snake_case (Parser-Service/Program.cs).
    // Use the same options here for both request bodies and response parsing.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<ParserProxyListResponse> ListProxiesAsync(bool? enabledOnly, CancellationToken ct)
    {
        var query = enabledOnly is not null ? $"?enabled_only={enabledOnly.ToString()!.ToLowerInvariant()}" : string.Empty;
        var response = await httpClient.GetAsync($"api/proxies{query}", ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserProxyListResponse>(JsonOptions, ct))!;
    }

    public async Task<ParserProxyDto> CreateProxyAsync(CreateParserProxyRequest request, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync("api/proxies", request, JsonOptions, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserProxyDto>(JsonOptions, ct))!;
    }

    public async Task DeleteProxyAsync(int id, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync("api/proxies/delete", new ParserProxyIdRequest(id), JsonOptions, ct);
        await EnsureSuccess(response, ct);
    }

    public Task<ParserProxyDto> DisableProxyAsync(int id, CancellationToken ct)
        => PostProxyAction("api/proxies/disable", id, ct);

    public Task<ParserProxyDto> EnableProxyAsync(int id, CancellationToken ct)
        => PostProxyAction("api/proxies/enable", id, ct);

    public Task<ParserProxyDto> ResetProxyHealthAsync(int id, CancellationToken ct)
        => PostProxyAction("api/proxies/reset-health", id, ct);

    public async Task<ParserProxyDto> SetProxyExpiresAtAsync(int id, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var payload = new SetParserProxyExpiresAtPayload(id, expiresAt);
        var response = await httpClient.PostAsJsonAsync("api/proxies/set-expires-at", payload, JsonOptions, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserProxyDto>(JsonOptions, ct))!;
    }

    public async Task<ParserCollectionTaskListResponse> ListTasksAsync(
        string? status, string? source, int? limit, int? offset, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrEmpty(source)) qs.Add($"source={Uri.EscapeDataString(source)}");
        if (limit is not null) qs.Add($"limit={limit}");
        if (offset is not null) qs.Add($"offset={offset}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty;

        var response = await httpClient.GetAsync($"api/collection-tasks{query}", ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserCollectionTaskListResponse>(JsonOptions, ct))!;
    }

    public async Task<ParserCollectionTaskStatusResponse> GetTaskStatusAsync(Guid taskId, CancellationToken ct)
    {
        var response = await httpClient.GetAsync($"api/collection-tasks/{taskId}", ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserCollectionTaskStatusResponse>(JsonOptions, ct))!;
    }

    public async Task<CreateParserCollectionTaskResponse> CreateTaskAsync(
        CreateParserCollectionTaskRequest request, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync("api/collection-tasks", request, JsonOptions, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<CreateParserCollectionTaskResponse>(JsonOptions, ct))!;
    }

    public async Task<ParserSearchResponse> SearchAsync(ParserSearchRequest request, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync("api/collection-tasks/search", request, JsonOptions, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserSearchResponse>(JsonOptions, ct))!;
    }

    private async Task<ParserProxyDto> PostProxyAction(string path, int id, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync(path, new ParserProxyIdRequest(id), JsonOptions, ct);
        await EnsureSuccess(response, ct);
        return (await response.Content.ReadFromJsonAsync<ParserProxyDto>(JsonOptions, ct))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ParserServiceException(response.StatusCode, body);
    }
}

public sealed class ParserServiceException(HttpStatusCode statusCode, string body)
    : HttpRequestException($"Parser-Service returned {(int)statusCode}: {body}", inner: null, statusCode: statusCode)
{
    public string Body { get; } = body;
}
