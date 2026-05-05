using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;

namespace ProcessingGateway.Application.Pipeline;

/// Решение №6 Этапа 0: dev-стенду нужно, чтобы pipeline доходил до `completed` без
/// Web API. Включается флагом `Pipeline:AutoFinalizeWithoutAggregates=true`.
/// Каждые `Pipeline:AutoFinalizeIntervalSeconds` ищет jobs в `computing_aggregates`
/// старше `Pipeline:AutoFinalizeAfterMinutes` и финализирует — то же, что сделал бы
/// `AggregatesReadyEventConsumer` от Web API.
///
/// На VPS-проде с Web API флаг = false; этот сервис в `ExecuteAsync` сразу выходит.
public sealed class AutoFinalizeService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<AutoFinalizeService> _logger;

    public AutoFinalizeService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<AutoFinalizeService> logger)
    {
        _services = services;
        _logger = logger;
        _enabled = bool.TryParse(configuration["Pipeline:AutoFinalizeWithoutAggregates"], out var e) && e;
        _interval = TimeSpan.FromSeconds(
            int.TryParse(configuration["Pipeline:AutoFinalizeIntervalSeconds"], out var i) ? i : 30);
        _staleAfter = TimeSpan.FromMinutes(
            int.TryParse(configuration["Pipeline:AutoFinalizeAfterMinutes"], out var m) ? m : 5);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            _logger.LogInformation("AutoFinalizeService disabled — Web API Analytics ожидается через брокер");
            return;
        }

        _logger.LogWarning(
            "AutoFinalizeService ENABLED (dev mode). Jobs в computing_aggregates старше {Stale} будут финализированы автоматически каждые {Interval}.",
            _staleAfter, _interval);

        while (!ct.IsCancellationRequested)
        {
            try { await FinalizeStaleAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "AutoFinalize cycle failed");
            }

            try { await Task.Delay(_interval, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task FinalizeStaleAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcessingDbContext>();

        var threshold = DateTimeOffset.UtcNow - _staleAfter;
        var stale = await db.AnalysisJobs
            .Where(j => j.Status == AnalysisJobStatus.ComputingAggregates
                     && j.SentAt != null
                     && j.SentAt < threshold)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        foreach (var job in stale)
        {
            var partial = job.CollectionProgress.Values.Any(e => e.Status == "failed");
            job.Status = partial ? AnalysisJobStatus.Partial : AnalysisJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("AutoFinalize processed {Count} jobs", stale.Count);
    }
}
