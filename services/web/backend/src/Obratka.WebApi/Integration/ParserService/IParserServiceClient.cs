using Obratka.WebApi.Integration.ParserService.Contracts;

namespace Obratka.WebApi.Integration.ParserService;

public interface IParserServiceClient
{
    // Proxies
    Task<ParserProxyListResponse> ListProxiesAsync(bool? enabledOnly, CancellationToken ct);
    Task<ParserProxyDto> CreateProxyAsync(CreateParserProxyRequest request, CancellationToken ct);
    Task DeleteProxyAsync(int id, CancellationToken ct);
    Task<ParserProxyDto> DisableProxyAsync(int id, CancellationToken ct);
    Task<ParserProxyDto> EnableProxyAsync(int id, CancellationToken ct);
    Task<ParserProxyDto> ResetProxyHealthAsync(int id, CancellationToken ct);

    // Collection tasks
    Task<ParserCollectionTaskListResponse> ListTasksAsync(
        string? status, string? source, int? limit, int? offset, CancellationToken ct);
    Task<ParserCollectionTaskStatusResponse> GetTaskStatusAsync(Guid taskId, CancellationToken ct);
    Task<CreateParserCollectionTaskResponse> CreateTaskAsync(
        CreateParserCollectionTaskRequest request, CancellationToken ct);
}
