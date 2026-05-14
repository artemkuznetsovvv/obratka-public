using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Contracts.Admin;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Controllers.Admin;

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/admin/companies")]
public sealed class AdminCompaniesController(WebApiDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(AdminCompanyListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminCompanyListResponse>> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var skip = Math.Max(offset, 0);

        var query = from c in db.Companies.AsNoTracking()
                    join u in db.Users.AsNoTracking() on c.OwnerUserId equals u.Id
                    select new { c, u };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.c.Name, pattern) ||
                (x.u.Email != null && EF.Functions.ILike(x.u.Email, pattern)) ||
                EF.Functions.ILike(x.u.FullName, pattern));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(x => new AdminCompanyListItem(
                x.c.Id,
                x.c.Name,
                x.c.Category,
                x.c.Subcategory,
                x.c.Cities,
                x.c.Branches.Count,
                x.c.Branches.Count(b => b.IsSelected),
                x.u.Id,
                x.u.Email ?? string.Empty,
                x.u.FullName,
                x.c.CreatedAt,
                x.c.UpdatedAt))
            .ToListAsync(ct);

        return Ok(new AdminCompanyListResponse(total, take, skip, items));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AdminCompanyDetails), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminCompanyDetails>> Get(Guid id, CancellationToken ct)
    {
        var data = await (from c in db.Companies.AsNoTracking()
                          join u in db.Users.AsNoTracking() on c.OwnerUserId equals u.Id
                          where c.Id == id
                          select new { c, u })
            .SingleOrDefaultAsync(ct);
        if (data is null) return NotFound();

        var branches = await db.CompanyBranches.AsNoTracking()
            .Where(b => b.CompanyId == id)
            .OrderByDescending(b => b.IsSelected)
            .ThenBy(b => b.City).ThenBy(b => b.Source).ThenBy(b => b.Name)
            .Select(b => new AdminCompanyBranchDto(
                b.Id, b.Source, b.ExternalId, b.ExternalUrl, b.Name, b.Address, b.City,
                b.Rating, b.ReviewCount, b.IsSelected, b.CreatedAt))
            .ToListAsync(ct);

        return Ok(new AdminCompanyDetails(
            data.c.Id,
            data.c.Name,
            data.c.Category,
            data.c.Subcategory,
            data.c.Cities,
            data.c.Description,
            data.u.Id,
            data.u.Email ?? string.Empty,
            data.u.FullName,
            data.c.CreatedAt,
            data.c.UpdatedAt,
            branches));
    }
}
