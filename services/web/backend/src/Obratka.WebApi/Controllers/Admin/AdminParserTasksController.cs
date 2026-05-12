using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Integration.ParserService;
using Obratka.WebApi.Integration.ParserService.Contracts;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/parser-tasks")]
public sealed class AdminParserTasksController(IParserServiceClient parser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ParserCollectionTaskListResponse), StatusCodes.Status200OK)]
    public Task<ParserCollectionTaskListResponse> List(
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
        => parser.ListTasksAsync(status, source, limit, offset, ct);

    [HttpGet("{taskId:guid}")]
    [ProducesResponseType(typeof(ParserCollectionTaskStatusResponse), StatusCodes.Status200OK)]
    public Task<ParserCollectionTaskStatusResponse> Get(Guid taskId, CancellationToken ct)
        => parser.GetTaskStatusAsync(taskId, ct);

    [HttpPost]
    [ProducesResponseType(typeof(CreateParserCollectionTaskResponse), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<CreateParserCollectionTaskResponse>> Create(
        [FromBody] CreateParserCollectionTaskRequest request,
        CancellationToken ct)
    {
        var result = await parser.CreateTaskAsync(request, ct);
        return Accepted($"api/admin/parser-tasks/{result.TaskId}", result);
    }
}
