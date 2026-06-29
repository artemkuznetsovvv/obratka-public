using Microsoft.AspNetCore.Mvc;
using ParserService.Api.Contracts;
using ParserService.Core.Models;
using ParserService.Infrastructure.Proxy;

namespace ParserService.Api;

[ApiController]
[Route("api/proxies")]
[RequireQaApiKey]
public class ProxiesController : ControllerBase
{
    private readonly IProxyRepository _repository;
    private readonly ILogger<ProxiesController> _logger;

    public ProxiesController(IProxyRepository repository, ILogger<ProxiesController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ProxyListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProxyListResponse>> List(
        [FromQuery] bool? enabled_only,
        CancellationToken ct)
    {
        var rows = await _repository.ListAsync(enabled_only, ct);
        var items = rows.Select(ToDto).ToList();
        return Ok(new ProxyListResponse(items.Count, items));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ProxyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProxyDto>> Create(
        [FromBody] CreateProxyRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
            return BadRequest(new { error = "host is required" });
        if (request.Port is <= 0 or > 65535)
            return BadRequest(new { error = "port must be 1..65535" });

        string protocol;
        try { protocol = DbProxyRotator.NormalizeProtocol(request.Protocol); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }

        var entity = new ProxyEntity
        {
            Host = request.Host.Trim(),
            Port = request.Port,
            Protocol = protocol,
            Username = string.IsNullOrEmpty(request.Username) ? null : request.Username,
            Password = string.IsNullOrEmpty(request.Password) ? null : request.Password,
            Notes = request.Notes,
            Enabled = request.Enabled ?? true,
            ExpiresAt = request.ExpiresAt
        };

        try
        {
            await _repository.AddAsync(entity, ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Conflict(new { error = "Proxy with the same host/port/username already exists" });
        }

        _logger.LogInformation("Proxy {Id} added: {Host}:{Port}", entity.Id, entity.Host, entity.Port);
        return CreatedAtAction(nameof(List), new { }, ToDto(entity));
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromBody] ProxyIdRequest request, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return NotFound();
        await _repository.DeleteAsync(request.Id, ct);
        _logger.LogInformation("Proxy {Id} deleted ({Host}:{Port})", request.Id, entity.Host, entity.Port);
        return NoContent();
    }

    [HttpPost("disable")]
    [ProducesResponseType(typeof(ProxyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProxyDto>> Disable([FromBody] ProxyIdRequest request, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return NotFound();
        await _repository.SetEnabledAsync(request.Id, false, ct);
        _logger.LogInformation("Proxy {Id} disabled ({Host}:{Port})", request.Id, entity.Host, entity.Port);
        var updated = await _repository.GetByIdAsync(request.Id, ct);
        return Ok(ToDto(updated!));
    }

    [HttpPost("enable")]
    [ProducesResponseType(typeof(ProxyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProxyDto>> Enable([FromBody] ProxyIdRequest request, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return NotFound();
        await _repository.SetEnabledAsync(request.Id, true, ct);
        _logger.LogInformation("Proxy {Id} enabled ({Host}:{Port})", request.Id, entity.Host, entity.Port);
        var updated = await _repository.GetByIdAsync(request.Id, ct);
        return Ok(ToDto(updated!));
    }

    [HttpPost("reset-health")]
    [ProducesResponseType(typeof(ProxyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProxyDto>> ResetHealth([FromBody] ProxyIdRequest request, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return NotFound();
        await _repository.ResetHealthAsync(request.Id, ct);
        _logger.LogInformation("Proxy {Id} health reset", request.Id);
        var updated = await _repository.GetByIdAsync(request.Id, ct);
        return Ok(ToDto(updated!));
    }

    [HttpPost("set-expires-at")]
    [ProducesResponseType(typeof(ProxyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProxyDto>> SetExpiresAt(
        [FromBody] SetProxyExpiresAtRequest request,
        CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return NotFound();
        await _repository.SetExpiresAtAsync(request.Id, request.ExpiresAt, ct);
        _logger.LogInformation(
            "Proxy {Id} expires_at set to {ExpiresAt} ({Host}:{Port})",
            request.Id, request.ExpiresAt, entity.Host, entity.Port);
        var updated = await _repository.GetByIdAsync(request.Id, ct);
        return Ok(ToDto(updated!));
    }

    private static ProxyDto ToDto(ProxyEntity e) => new(
        e.Id, e.Host, e.Port, e.Protocol, e.Username, e.Enabled,
        e.FailureCount, e.CooldownUntil, e.LastUsedAt, e.ExpiresAt, e.Notes,
        e.CreatedAt, e.UpdatedAt);
}
