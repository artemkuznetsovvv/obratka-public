using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public class StubPerSourceRateLimiter : IPerSourceRateLimiter
{
    public Task WaitAsync(SourceType source, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
