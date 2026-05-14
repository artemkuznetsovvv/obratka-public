using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Contracts.Admin;
using Obratka.WebApi.Data;
// StartAdminAnalysisRequest / RestartSourceAdminRequest from Obratka.WebApi.Contracts.Admin
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/analyses")]
public sealed class AdminAnalysesController(
    IProcessingGatewayClient gateway,
    WebApiDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AnalysisJobListResponse), StatusCodes.Status200OK)]
    public Task<AnalysisJobListResponse> List(
        [FromQuery] string? status,
        [FromQuery] Guid? companyId,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
        => gateway.ListAnalysesAsync(status, companyId, limit, offset, ct);

    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(AnalysisJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisJobDto>> Get(Guid jobId, CancellationToken ct)
    {
        var result = await gateway.GetAnalysisAsync(jobId, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(StartAnalysisQaResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StartAnalysisQaResponse>> Start(
        [FromBody] StartAdminAnalysisRequest request,
        CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == request.CompanyId, ct);
        if (company is null) return NotFound(new { error = "Company not found" });

        var branches = await db.CompanyBranches.AsNoTracking()
            .Where(b => b.CompanyId == request.CompanyId
                        && b.IsSelected
                        && b.ExternalId != ""
                        && b.ExternalUrl != null)
            .Select(b => new StartAnalysisBranchSpec(b.Id, b.Source, b.ExternalId, b.ExternalUrl!))
            .ToListAsync(ct);
        if (branches.Count == 0)
            return BadRequest(new { error = "Company has no selected branches with externalId/externalUrl" });

        var qaRequest = new StartAnalysisQaRequest(
            request.CompanyId, request.DateFrom, request.DateTo, branches);
        var response = await gateway.StartAnalysisAsync(qaRequest, ct);
        return Accepted($"/api/admin/analyses/{response.AnalysisJobId}", response);
    }

    [HttpPost("{jobId:guid}/restart-source/{source}")]
    [ProducesResponseType(typeof(RestartSourceQaResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RestartSourceQaResponse>> RestartSource(
        Guid jobId,
        string source,
        [FromBody] RestartSourceAdminRequest request,
        CancellationToken ct)
    {
        if (!BranchSources.IsKnown(source))
            return BadRequest(new { error = $"Unknown source slug: {source}" });

        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound(new { error = "Analysis job not found" });

        var branches = await db.CompanyBranches.AsNoTracking()
            .Where(b => b.CompanyId == job.CompanyId
                        && b.IsSelected
                        && b.Source == source
                        && b.ExternalId != ""
                        && b.ExternalUrl != null)
            .Select(b => new RestartSourceBranchSpec(b.Id, b.ExternalId, b.ExternalUrl!))
            .ToListAsync(ct);
        if (branches.Count == 0)
            return BadRequest(new { error = $"No selected branches for source '{source}' on this company" });

        var qaRequest = new RestartSourceQaRequest(branches, request.DateFrom, request.DateTo);
        var response = await gateway.RestartSourceAsync(jobId, source, qaRequest, ct);
        return Accepted(response);
    }

    [HttpPost("{jobId:guid}/llm-replay")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LlmReplay(Guid jobId, CancellationToken ct)
    {
        var job = await gateway.GetAnalysisAsync(jobId, ct);
        if (job is null) return NotFound();
        await gateway.LlmReplayAsync(jobId, ct);
        return Accepted();
    }

    [HttpGet("{jobId:guid}/blobs")]
    [ProducesResponseType(typeof(JobBlobList), StatusCodes.Status200OK)]
    public Task<JobBlobList> ListBlobs(Guid jobId, CancellationToken ct)
        => gateway.ListJobBlobsAsync(jobId, ct);

    // PG accepts only a closed set of names: input, output_reviews, output_summary, raw/<source>.
    [HttpGet("{jobId:guid}/blobs/{*name}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadBlob(Guid jobId, string name, CancellationToken ct)
    {
        var blob = await gateway.DownloadJobBlobAsync(jobId, name, ct);
        if (blob is null) return NotFound();
        return File(blob.Stream, blob.ContentType, blob.FileName ?? $"{jobId}-{name.Replace('/', '-')}.json");
    }
}
