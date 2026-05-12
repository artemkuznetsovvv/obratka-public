using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/analyses")]
public sealed class AdminAnalysesController(IProcessingGatewayClient gateway) : ControllerBase
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
}
