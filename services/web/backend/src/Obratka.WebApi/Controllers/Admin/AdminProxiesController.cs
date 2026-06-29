using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Integration.ParserService;
using Obratka.WebApi.Integration.ParserService.Contracts;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/proxies")]
public sealed class AdminProxiesController(IParserServiceClient parser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ParserProxyListResponse), StatusCodes.Status200OK)]
    public Task<ParserProxyListResponse> List([FromQuery] bool? enabledOnly, CancellationToken ct)
        => parser.ListProxiesAsync(enabledOnly, ct);

    [HttpPost]
    [ProducesResponseType(typeof(ParserProxyDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<ParserProxyDto>> Create([FromBody] CreateParserProxyRequest request, CancellationToken ct)
    {
        var created = await parser.CreateProxyAsync(request, ct);
        return Created($"api/admin/proxies/{created.Id}", created);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await parser.DeleteProxyAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/disable")]
    [ProducesResponseType(typeof(ParserProxyDto), StatusCodes.Status200OK)]
    public Task<ParserProxyDto> Disable(int id, CancellationToken ct) => parser.DisableProxyAsync(id, ct);

    [HttpPost("{id:int}/enable")]
    [ProducesResponseType(typeof(ParserProxyDto), StatusCodes.Status200OK)]
    public Task<ParserProxyDto> Enable(int id, CancellationToken ct) => parser.EnableProxyAsync(id, ct);

    [HttpPost("{id:int}/reset-health")]
    [ProducesResponseType(typeof(ParserProxyDto), StatusCodes.Status200OK)]
    public Task<ParserProxyDto> ResetHealth(int id, CancellationToken ct) => parser.ResetProxyHealthAsync(id, ct);

    [HttpPost("{id:int}/set-expires-at")]
    [ProducesResponseType(typeof(ParserProxyDto), StatusCodes.Status200OK)]
    public Task<ParserProxyDto> SetExpiresAt(
        int id,
        [FromBody] SetParserProxyExpiresAtRequest request,
        CancellationToken ct)
        => parser.SetProxyExpiresAtAsync(id, request.ExpiresAt, ct);
}
