using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Domain;
using ProcessingGateway.Infrastructure.Database;

namespace ProcessingGateway.Api;

/// Публичный (изнутри docker network) Status API для progress screen Web API/Frontend.
/// Контракт — CLAUDE.md «HTTP Status API».
///
/// На MVP без Web API — пробрасывается через nginx-vhost `gateway-dev` с X-Api-Key
/// (решение №5 Этапа 0). С приходом Web API эндпоинт остаётся внутренним, ходит к нему
/// только Analytics-модуль и BFF-frontend-роуты.
[ApiController]
[Route("api/analyses")]
public sealed class AnalysisStatusController : ControllerBase
{
    private readonly ProcessingDbContext _db;

    public AnalysisStatusController(ProcessingDbContext db) => _db = db;

    public sealed record StageDto(string Key, string Label, string State);
    public sealed record SourceDto(string Status, int Progress, int? ReviewCount);
    public sealed record StatusResponse(
        string Status,
        IReadOnlyList<StageDto> Stages,
        IReadOnlyDictionary<string, SourceDto> Sources,
        int ReviewCount);

    [HttpGet("{jobId:guid}/status")]
    [ProducesResponseType(typeof(StatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StatusResponse>> Get(Guid jobId, CancellationToken ct)
    {
        var job = await _db.AnalysisJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null) return NotFound();

        var stages = BuildStages(job.Status);
        var sources = job.CollectionProgress.ToDictionary(
            kv => kv.Key,
            kv => new SourceDto(
                Status: kv.Value.Status,
                Progress: kv.Value.Progress,
                ReviewCount: kv.Value.ReviewCount));

        return Ok(new StatusResponse(
            Status: job.Status.ToWire(),
            Stages: stages,
            Sources: sources,
            ReviewCount: job.ReviewCount));
    }

    /// Раскраска стадий по `analysis_jobs.status` (CLAUDE.md «HTTP Status API»).
    /// 3 стадии: collecting, llm_analysis, building_dashboard. State ∈ pending/active/completed/failed.
    private static IReadOnlyList<StageDto> BuildStages(AnalysisJobStatus status)
    {
        // Помощник: state каждой стадии при данном глобальном status.
        string Collecting() => status switch
        {
            AnalysisJobStatus.Pending => "pending",
            AnalysisJobStatus.Collecting => "active",
            AnalysisJobStatus.Failed => "failed",
            _ => "completed"        // sent_to_llm, computing_aggregates, completed, partial — сбор позади
        };

        string LlmAnalysis() => status switch
        {
            AnalysisJobStatus.Pending or AnalysisJobStatus.Collecting => "pending",
            AnalysisJobStatus.SentToLlm => "active",
            AnalysisJobStatus.Failed => "failed",
            _ => "completed"
        };

        string BuildingDashboard() => status switch
        {
            AnalysisJobStatus.Pending
                or AnalysisJobStatus.Collecting
                or AnalysisJobStatus.SentToLlm => "pending",
            AnalysisJobStatus.ComputingAggregates => "active",
            AnalysisJobStatus.Completed or AnalysisJobStatus.Partial => "completed",
            AnalysisJobStatus.Failed => "failed",
            _ => "pending"
        };

        return new StageDto[]
        {
            new("collecting",         "Сбор отзывов",                          Collecting()),
            new("llm_analysis",       "Анализ (фейки, тональность, темы)",     LlmAnalysis()),
            new("building_dashboard", "Построение дашборда",                   BuildingDashboard())
        };
    }
}
