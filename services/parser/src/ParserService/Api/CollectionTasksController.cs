using Microsoft.AspNetCore.Mvc;
using ParserService.Api.Contracts;
using ParserService.Core;
using ParserService.Core.Models;

namespace ParserService.Api;

[ApiController]
[Route("api/collection-tasks")]
public class CollectionTasksController : ControllerBase
{
    private readonly CollectionTaskOrchestrator _orchestrator;
    private readonly ITaskRepository _repository;

    public CollectionTasksController(
        CollectionTaskOrchestrator orchestrator,
        ITaskRepository repository)
    {
        _orchestrator = orchestrator;
        _repository = repository;
    }

    [HttpPost("search")]
    [ProducesResponseType(typeof(SearchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromBody] SearchRequest request, CancellationToken ct)
    {
        var sources = request.Sources
            .Where(s => SourceTypeExtensions.TryFromSlug(s, out _))
            .Select(s => SourceTypeExtensions.FromSlug(s))
            .ToArray();

        var coreRequest = new CompanySearchRequest(request.Query, request.City, sources);
        var results = await _orchestrator.SearchAsync(coreRequest, ct);

        var dtos = results.Select(r => new SearchBranchResultDto(
            r.Source.ToSlug(), r.ExternalId, r.ExternalUrl,
            r.Name, r.Address, r.Rating, r.ReviewCount
        )).ToList();

        return Ok(new SearchResponse(dtos));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateCollectionTaskResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateCollectionTaskResponse>> Create(
        [FromBody] CreateCollectionTaskRequest request, CancellationToken ct)
    {
        if (!SourceTypeExtensions.TryFromSlug(request.Source, out _))
            return BadRequest(new { error = $"Unknown source: '{request.Source}'" });

        if (request.Branches is not { Count: > 0 })
            return BadRequest(new { error = "At least one branch is required" });

        var taskId = await _orchestrator.StartCollectionAsync(request, ct);
        return AcceptedAtAction(nameof(GetStatus), new { taskId }, new CreateCollectionTaskResponse(taskId));
    }

    [HttpGet("{taskId:guid}")]
    [ProducesResponseType(typeof(CollectionTaskStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CollectionTaskStatusResponse>> GetStatus(
        Guid taskId, CancellationToken ct)
    {
        var task = await _repository.GetByIdAsync(taskId, ct);
        if (task is null)
            return NotFound();

        var response = new CollectionTaskStatusResponse(
            task.Id,
            task.Status.ToString().ToLowerInvariant(),
            task.Source.ToSlug(),
            task.Progress,
            task.ReviewCount,
            task.S3Url,
            task.Error);

        return Ok(response);
    }
}
