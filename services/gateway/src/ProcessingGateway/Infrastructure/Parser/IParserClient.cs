using ProcessingGateway.Infrastructure.Parser.Contracts;

namespace ProcessingGateway.Infrastructure.Parser;

/// HTTP-фасад Parser Service. Скачивание `raw/{source}.json` из S3 — отдельный
/// `IJobBlobStorage`, чтобы парсер-клиент не знал про S3 (separation of concerns).
public interface IParserClient
{
    /// `POST /api/collection-tasks` → 202 Accepted + taskId.
    Task<Guid> StartCollectionAsync(StartCollectionRequest request, CancellationToken ct = default);

    /// `GET /api/collection-tasks/{taskId}`. 404 → ParserTaskNotFoundException.
    Task<CollectionTaskStatusResponse> GetStatusAsync(Guid taskId, CancellationToken ct = default);
}

public sealed class ParserTaskNotFoundException(Guid taskId)
    : Exception($"Parser task {taskId} not found");
