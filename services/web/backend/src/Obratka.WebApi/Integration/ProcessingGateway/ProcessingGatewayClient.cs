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

    public async Task<StartAnalysisQaResponse> StartAnalysisAsync(StartAnalysisQaRequest request, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync("api/qa/analyses", request, ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadFromJsonAsync<RawStartAnalysisResponse>(ct)
                  ?? throw new InvalidOperationException("Processing-Gateway returned empty start-analysis response");
        return new StartAnalysisQaResponse(raw.AnalysisJobId);
    }

    public async Task<RestartSourceQaResponse> RestartSourceAsync(
        Guid jobId, string source, RestartSourceQaRequest request, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"api/qa/parser/restart-source/{jobId}/{Uri.EscapeDataString(source)}", request, ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadFromJsonAsync<RawRestartSourceResponse>(ct)
                  ?? throw new InvalidOperationException("Processing-Gateway returned empty restart-source response");
        return new RestartSourceQaResponse(raw.Source, raw.TaskId, raw.PreviousStatus, raw.CurrentStatus);
    }

    public async Task LlmReplayAsync(Guid jobId, CancellationToken ct)
    {
        var response = await httpClient.PostAsync($"api/qa/llm/replay/{jobId}", content: null, ct);
        await EnsureSuccess(response, ct);
    }

    public async Task<JobBlobList> ListJobBlobsAsync(Guid jobId, CancellationToken ct)
    {
        var response = await httpClient.GetAsync($"api/qa/jobs/{jobId}/blobs", ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadFromJsonAsync<RawJobBlobList>(ct)
                  ?? throw new InvalidOperationException("Processing-Gateway returned empty blob list");
        return new JobBlobList(
            raw.Bucket,
            raw.Prefix,
            raw.Count,
            raw.Items.Select(i => new JobBlobItem(i.Key, i.Size, i.LastModified)).ToList());
    }

    public async Task<JobBlobContent?> DownloadJobBlobAsync(Guid jobId, string name, CancellationToken ct)
    {
        // Stream the body straight through — files can be tens of MB (raw/<source>.json).
        var response = await httpClient.GetAsync(
            $"api/qa/jobs/{jobId}/blobs/{name}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, ct);

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        var length = response.Content.Headers.ContentLength;
        return new JobBlobContent(stream, contentType, fileName, length);
    }

    public async Task<IReadOnlyList<BranchStatsItem>> GetBranchStatsAsync(
        Guid jobId, CancellationToken ct)
    {
        var response = await httpClient.GetAsync($"api/qa/analyses/{jobId}/branch-stats", ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadFromJsonAsync<RawBranchStatsResponse>(ct);
        if (raw is null) return [];
        return raw.Items
            .Select(i => new BranchStatsItem(i.BranchId, i.Source, i.ReviewCount))
            .ToList();
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
