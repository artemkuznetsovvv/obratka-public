using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Parser;
using ProcessingGateway.Infrastructure.Parser.Contracts;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Consumers;

/// Слушает команду «запустить анализ». Создаёт `analysis_jobs(status=pending)`,
/// переводит в `collecting`, разбивает branches по source-ам и для каждого
/// вызывает Parser. `task_id` от Parser-а сохраняется в
/// `collection_progress[source]`. После этого ParserPoller (IHostedService)
/// подхватит активные задачи.
///
/// Идемпотентность: если job с этим Id уже существует — лог + skip без падения.
/// Это даёт MassTransit retry повторно доставить сообщение и при этом не
/// двоить parser-таски.
public sealed class StartAnalysisCommandConsumer : IConsumer<StartAnalysisCommand>
{
    private readonly ProcessingDbContext _db;
    private readonly IParserClient _parser;
    private readonly ILogger<StartAnalysisCommandConsumer> _logger;

    public StartAnalysisCommandConsumer(
        ProcessingDbContext db,
        IParserClient parser,
        ILogger<StartAnalysisCommandConsumer> logger)
    {
        _db = db;
        _parser = parser;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StartAnalysisCommand> context)
    {
        var msg = context.Message;
        using var _ = LogContext.PushProperty("AnalysisJobId", msg.AnalysisJobId);
        using var __ = LogContext.PushProperty("CompanyId", msg.CompanyId);

        _logger.LogInformation(
            "StartAnalysisCommand received: branches={BranchCount}, sources={Sources}",
            msg.Branches.Count, string.Join(",", msg.Branches.Select(b => b.Source).Distinct()));

        var existing = await _db.AnalysisJobs.FirstOrDefaultAsync(
            j => j.Id == msg.AnalysisJobId, context.CancellationToken);

        if (existing is not null)
        {
            _logger.LogWarning(
                "Job {AnalysisJobId} already exists with status={Status}, skipping duplicate command",
                msg.AnalysisJobId, existing.Status);
            return;
        }

        if (msg.Branches.Count == 0)
        {
            _logger.LogError("StartAnalysisCommand has empty branches — failing job immediately");
            _db.AnalysisJobs.Add(new AnalysisJob
            {
                Id = msg.AnalysisJobId,
                CompanyId = msg.CompanyId,
                Status = AnalysisJobStatus.Failed,
                Error = "No branches supplied",
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        var job = new AnalysisJob
        {
            Id = msg.AnalysisJobId,
            CompanyId = msg.CompanyId,
            Status = AnalysisJobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            CollectionProgress = new Dictionary<string, CollectionProgressEntry>()
        };
        _db.AnalysisJobs.Add(job);
        // Сохраняем pending-job ДО запуска parser-задач, чтобы при падении ParserClient
        // job не был потерян (poller увидит и сможет вытащить через QA-restart).
        await _db.SaveChangesAsync(context.CancellationToken);

        // Группируем branches по source — один POST на source (ADR-001 §4).
        var bySource = msg.Branches.GroupBy(b => b.Source);

        var collectionProgress = new Dictionary<string, CollectionProgressEntry>();
        var failedSources = new List<string>();

        foreach (var group in bySource)
        {
            var source = group.Key;
            var branches = group
                .Select(b => new BranchTargetDto(b.BranchId, b.ExternalId, b.ExternalUrl))
                .ToList();

            try
            {
                var taskId = await _parser.StartCollectionAsync(new StartCollectionRequest(
                    JobId: msg.AnalysisJobId,
                    CompanyId: msg.CompanyId,
                    Source: source,
                    DateFrom: msg.DateFrom,
                    DateTo: msg.DateTo,
                    Branches: branches), context.CancellationToken);

                collectionProgress[source] = new CollectionProgressEntry
                {
                    TaskId = taskId,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = "pending",
                    Progress = 0
                };

                _logger.LogInformation(
                    "Parser task created: source={Source} taskId={TaskId} branches={BranchCount}",
                    source, taskId, branches.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start Parser task for source={Source}; marking source as failed",
                    source);
                collectionProgress[source] = new CollectionProgressEntry
                {
                    Status = "failed",
                    Progress = 0,
                    Error = ex.Message
                };
                failedSources.Add(source);
            }
        }

        // Все источники упали ещё на старте → переводим job сразу в failed.
        // ParserPoller дальше нечего ловить.
        if (failedSources.Count == bySource.Count())
        {
            job.Status = AnalysisJobStatus.Failed;
            job.Error = $"All sources failed to start: {string.Join(", ", failedSources)}";
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            job.Status = AnalysisJobStatus.Collecting;
        }

        job.CollectionProgress = collectionProgress;
        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Job {AnalysisJobId} → status={Status}", msg.AnalysisJobId, job.Status);
    }
}
