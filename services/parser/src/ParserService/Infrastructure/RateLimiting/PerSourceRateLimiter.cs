using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public class PerSourceRateLimiter : IPerSourceRateLimiter
{
    private readonly ConcurrentDictionary<SourceType, SourceThrottleState> _states = new();
    private readonly RateLimitingOptions _options;
    private readonly ILogger<PerSourceRateLimiter> _logger;

    private static readonly SourceRateLimitOptions DefaultSourceOptions = new();

    public PerSourceRateLimiter(
        IOptions<RateLimitingOptions> options,
        ILogger<PerSourceRateLimiter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Inter-page delay (called inside a single org collection).
    /// </summary>
    public async Task WaitAsync(SourceType source, CancellationToken ct)
    {
        var opts = GetSourceOptions(source);
        var state = GetState(source, opts);

        await state.PageSemaphore.WaitAsync(ct);
        try
        {
            var delay = Random.Shared.Next(2000, 5001);
            await Task.Delay(delay, ct);
        }
        finally
        {
            state.PageSemaphore.Release();
        }
    }

    /// <summary>
    /// Acquire a slot to process one organization.
    /// Blocks until: (1) concurrency limit allows, (2) sliding window has capacity.
    /// </summary>
    public async Task AcquireOrgSlotAsync(SourceType source, CancellationToken ct)
    {
        var opts = GetSourceOptions(source);
        var state = GetState(source, opts);

        // 1) Wait for concurrency slot
        _logger.LogDebug("Waiting for org concurrency slot ({Source}, max {Max})",
            source, opts.MaxConcurrentOrgs);
        await state.ConcurrencySemaphore.WaitAsync(ct);

        // 2) Wait for sliding window capacity
        if (opts.MaxOrgsPerHour > 0)
        {
            await WaitForSlidingWindowAsync(state, opts.MaxOrgsPerHour, ct);
        }

        _logger.LogDebug("Org slot acquired for {Source}", source);
    }

    /// <summary>
    /// Release the org slot, record completion timestamp, apply inter-org delay.
    /// </summary>
    public async Task ReleaseOrgSlotAsync(SourceType source, CancellationToken ct)
    {
        var opts = GetSourceOptions(source);
        var state = GetState(source, opts);

        try
        {
            // Record completion for sliding window
            state.OrgCompletionTimes.Enqueue(DateTime.UtcNow);

            // Inter-org delay
            var delay = Random.Shared.Next(opts.DelayAfterOrgMinMs, opts.DelayAfterOrgMaxMs + 1);
            _logger.LogDebug("Inter-org delay {Delay}ms for {Source}", delay, source);
            await Task.Delay(delay, ct);
        }
        finally
        {
            state.ConcurrencySemaphore.Release();
            _logger.LogDebug("Org slot released for {Source}", source);
        }
    }

    private async Task WaitForSlidingWindowAsync(
        SourceThrottleState state, int maxPerHour, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var cutoff = DateTime.UtcNow.AddHours(-1);

            // Evict entries older than 1 hour
            while (state.OrgCompletionTimes.TryPeek(out var oldest) && oldest < cutoff)
                state.OrgCompletionTimes.TryDequeue(out _);

            if (state.OrgCompletionTimes.Count < maxPerHour)
                return;

            // Wait until the oldest entry expires
            if (state.OrgCompletionTimes.TryPeek(out var nextExpiry))
            {
                var waitMs = (int)(nextExpiry.AddHours(1) - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                {
                    _logger.LogInformation(
                        "Sliding window full ({Count}/{Max} orgs/hour for {Source}), waiting {Wait}ms",
                        state.OrgCompletionTimes.Count, maxPerHour, state.Source, waitMs);
                    await Task.Delay(waitMs + 100, ct); // +100ms buffer
                }
            }
        }
    }

    private SourceRateLimitOptions GetSourceOptions(SourceType source)
    {
        var slug = source.ToSlug();
        return _options.Sources.TryGetValue(slug, out var opts) ? opts : DefaultSourceOptions;
    }

    private SourceThrottleState GetState(SourceType source, SourceRateLimitOptions opts)
    {
        return _states.GetOrAdd(source, _ => new SourceThrottleState(source, opts.MaxConcurrentOrgs));
    }

    private sealed class SourceThrottleState
    {
        public SourceType Source { get; }
        public SemaphoreSlim ConcurrencySemaphore { get; }
        public SemaphoreSlim PageSemaphore { get; } = new(1, 1);
        public ConcurrentQueue<DateTime> OrgCompletionTimes { get; } = new();

        public SourceThrottleState(SourceType source, int maxConcurrent)
        {
            Source = source;
            ConcurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }
    }
}
