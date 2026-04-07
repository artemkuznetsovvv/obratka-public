using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public interface IPerSourceRateLimiter
{
    Task WaitAsync(SourceType source, CancellationToken ct);
}
