using System.Collections.Concurrent;
using ParserService.Core.Models;

namespace ParserService.Infrastructure.RateLimiting;

public class PerSourceRateLimiter : IPerSourceRateLimiter
{
    private readonly ConcurrentDictionary<SourceType, SemaphoreSlim> _semaphores = new();
    private static readonly Random Rng = new();

    public async Task WaitAsync(SourceType source, CancellationToken ct)
    {
        var semaphore = _semaphores.GetOrAdd(source, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct);
        try
        {
            var delay = source switch
            {
                SourceType.YandexMaps => Rng.Next(2000, 5001),
                _ => Rng.Next(1000, 3001)
            };

            await Task.Delay(delay, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
