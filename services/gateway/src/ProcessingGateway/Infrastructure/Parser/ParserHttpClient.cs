using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ProcessingGateway.Infrastructure.Parser.Contracts;

namespace ProcessingGateway.Infrastructure.Parser;

public sealed class ParserHttpClient : IParserClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<ParserHttpClient> _logger;

    public ParserHttpClient(HttpClient http, ILogger<ParserHttpClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Guid> StartCollectionAsync(StartCollectionRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Parser StartCollection job={JobId} source={Source} branches={BranchCount}",
            request.JobId, request.Source, request.Branches.Count);

        using var response = await _http.PostAsJsonAsync("api/collection-tasks", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StartCollectionResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Parser returned empty body for StartCollection");

        return payload.TaskId;
    }

    public async Task<CollectionTaskStatusResponse> GetStatusAsync(Guid taskId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"api/collection-tasks/{taskId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ParserTaskNotFoundException(taskId);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CollectionTaskStatusResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException($"Parser returned empty body for GET {taskId}");
    }
}
