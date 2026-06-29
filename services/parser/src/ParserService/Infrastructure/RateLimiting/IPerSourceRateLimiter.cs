using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public interface IPerSourceRateLimiter
{
    /// <summary>
    /// Inter-page delay (2-5s for Yandex). Called inside a single org collection.
    /// </summary>
    Task WaitAsync(SourceType source, CancellationToken ct);

    /// <summary>
    /// Acquire a slot to process one organization.
    /// Blocks until concurrency limit and sliding-window throughput allow it.
    /// Must be paired with <see cref="ReleaseOrgSlotAsync"/>.
    /// </summary>
    Task AcquireOrgSlotAsync(SourceType source, CancellationToken ct);

    /// <summary>
    /// Release the organization slot and apply inter-org delay.
    /// </summary>
    Task ReleaseOrgSlotAsync(SourceType source, CancellationToken ct);
}
