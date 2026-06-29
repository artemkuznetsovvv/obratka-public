using ParserService.Core.Models;

namespace ParserService.Infrastructure.Storage;

public interface IS3ResultStorage
{
    Task<string> UploadResultAsync(CollectionResult result, CancellationToken ct);
}
