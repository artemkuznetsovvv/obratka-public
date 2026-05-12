using System.Net;
using System.Net.Http.Json;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Integration.ProcessingGateway;

internal sealed class ProcessingGatewayClient(HttpClient httpClient) : IProcessingGatewayClient
{
    public async Task<AnalysisJobListResponse> ListAnalysesAsync(
        string? status, Guid? companyId, int? limit, int? offset, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (companyId is not null) qs.Add($"companyId={companyId}");
        if (limit is not null) qs.Add($"limit={limit}");
        if (offset is not null) qs.Add($"offset={offset}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty;

        var response = await httpClient.GetAsync($"api/qa/analyses{query}", ct);
        await EnsureSuccess(response, ct);

        var raw = await response.Content.ReadFromJsonAsync<RawAnalysisJobListResponse>(ct)
                  ?? throw new InvalidOperationException("Processing-Gateway returned empty list response");

        return new AnalysisJobListResponse(
            raw.Total, raw.Limit, raw.Offset,
            raw.Items.Select(AnalysisJobMapping.ToDto).ToList());
    }

    public async Task<AnalysisJobDto?> GetAnalysisAsync(Guid jobId, CancellationToken ct)
    {
        var response = await httpClient.GetAsync($"api/qa/analyses/{jobId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, ct);

        var raw = await response.Content.ReadFromJsonAsync<RawAnalysisJob>(ct);
        return raw is null ? null : AnalysisJobMapping.ToDto(raw);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ProcessingGatewayException(response.StatusCode, body);
    }
}

public sealed class ProcessingGatewayException(HttpStatusCode statusCode, string body)
    : HttpRequestException($"Processing-Gateway returned {(int)statusCode}: {body}", inner: null, statusCode: statusCode)
{
    public string Body { get; } = body;
}
