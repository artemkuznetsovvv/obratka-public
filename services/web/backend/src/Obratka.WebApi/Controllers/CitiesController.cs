using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Contracts.Companies;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/cities")]
public sealed class CitiesController(WebApiDbContext db) : ControllerBase
{
    [HttpGet("suggest")]
    [ProducesResponseType(typeof(CitySuggestResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CitySuggestResponse>> Suggest(
        [FromQuery] string? q,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 50);
        var query = db.Cities.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            // ILIKE-prefix first, then contains — boosted via OrderBy:
            //   StartsWith → 0, Contains → 1
            query = query.Where(c => EF.Functions.Like(c.NameNormalized, needle + "%")
                                      || EF.Functions.Like(c.NameNormalized, "%" + needle + "%"));
            var items = await query
                .OrderBy(c => EF.Functions.Like(c.NameNormalized, needle + "%") ? 0 : 1)
                .ThenBy(c => c.Name)
                .Take(take)
                .Select(c => new CitySuggestion(c.Id, c.Name, c.Region))
                .ToListAsync(ct);
            return Ok(new CitySuggestResponse(items));
        }

        var all = await query
            .OrderBy(c => c.Name)
            .Take(take)
            .Select(c => new CitySuggestion(c.Id, c.Name, c.Region))
            .ToListAsync(ct);
        return Ok(new CitySuggestResponse(all));
    }
}
