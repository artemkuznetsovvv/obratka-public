using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public class StubPerSourceRateLimiter : IPerSourceRateLimiter
{
    public Task WaitAsync(SourceType source, CancellationToken ct)
        => Task.CompletedTask;

    public Task AcquireOrgSlotAsync(SourceType source, CancellationToken ct)
        => Task.CompletedTask;

    public Task ReleaseOrgSlotAsync(SourceType source, CancellationToken ct)
        => Task.CompletedTask;
}
