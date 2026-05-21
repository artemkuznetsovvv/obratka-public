using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Contracts.Companies;
using Obratka.WebApi.Data;

namespace Obratka.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/companies")]
public sealed class CompaniesController(
    WebApiDbContext db,
    UserManager<ApplicationUser> userManager,
    IBranchSearchService searchService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(CompanyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CompanyDto>> Create([FromBody] CreateCompanyRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var cities = request.Cities
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cities.Count == 0)
            return BadRequest(new { error = "At least one city is required" });

        var company = new Company
        {
            OwnerUserId = ownerId.Value,
            Name = request.Name.Trim(),
            Category = request.Category?.Trim(),
            Subcategory = request.Subcategory?.Trim(),
            Cities = cities,
            Description = request.Description?.Trim(),
        };

        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = company.Id }, ToDto(company, branchCount: 0));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CompanyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanyDto>> Update(
        Guid id, [FromBody] CreateCompanyRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var cities = request.Cities
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cities.Count == 0)
            return BadRequest(new { error = "At least one city is required" });

        var company = await db.Companies
            .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound();

        company.Name = request.Name.Trim();
        company.Category = request.Category?.Trim();
        company.Subcategory = request.Subcategory?.Trim();
        company.Cities = cities;
        company.Description = request.Description?.Trim();
        company.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var branchCount = await db.CompanyBranches.CountAsync(b => b.CompanyId == company.Id, ct);
        return Ok(ToDto(company, branchCount));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CompanyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanyDto>> Get(Guid id, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var company = await db.Companies
            .AsNoTracking()
            .Where(c => c.Id == id && c.OwnerUserId == ownerId)
            .Select(c => new
            {
                Company = c,
                BranchCount = c.Branches.Count,
            })
            .SingleOrDefaultAsync(ct);

        if (company is null) return NotFound();
        return Ok(ToDto(company.Company, company.BranchCount));
    }

    [HttpPost("{id:guid}/search")]
    [ProducesResponseType(typeof(BranchSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<BranchSearchResponse>> Search(
        Guid id,
        [FromQuery] string city,
        [FromQuery] string[]? sources,
        CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(city))
            return BadRequest(new { error = "city query parameter is required" });

        var company = await db.Companies
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound();

        var effectiveSources = sources is { Length: > 0 } ? sources : BranchSources.All;
        try
        {
            var result = await searchService.SearchAsync(company.Id, company.Name, city, effectiveSources, ct);
            return Ok(result);
        }
        catch (BranchSearchUnavailableException ex)
        {
            // 502 Bad Gateway = upstream (Parser-Service) ответил плохо или недоступен.
            // detail летит во фронт в data.detail — описано человеческим текстом.
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Поиск временно недоступен",
                detail: ex.Message);
        }
    }

    [HttpPost("{id:guid}/branches")]
    [ProducesResponseType(typeof(IReadOnlyList<CompanyBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CompanyBranchDto>>> SaveBranches(
        Guid id, [FromBody] SaveBranchesRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var company = await db.Companies
            .Include(c => c.Branches)
            .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound();

        var selectedIds = request.BranchIds.ToHashSet();
        if (selectedIds.Count == 0)
            return BadRequest(new { error = "No branch ids in request" });

        var ownedIds = company.Branches.Select(b => b.Id).ToHashSet();
        var unknown = selectedIds.Where(g => !ownedIds.Contains(g)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = $"Branch ids do not belong to this company: {string.Join(", ", unknown)}" });

        // Submission of step 2 fully overrides the picks: explicit branches become selected, the rest demote to candidate.
        foreach (var branch in company.Branches)
            branch.IsSelected = selectedIds.Contains(branch.Id);

        company.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var branches = company.Branches
            .Where(b => b.IsSelected)
            .OrderBy(b => b.City)
            .ThenBy(b => b.Source)
            .ThenBy(b => b.Name)
            .Select(b => new CompanyBranchDto(
                b.Id, b.Source, b.ExternalId, b.ExternalUrl, b.Name, b.Address, b.City, b.Rating, b.ReviewCount))
            .ToList();
        return Ok(branches);
    }

    [HttpGet("{id:guid}/branches")]
    [ProducesResponseType(typeof(IReadOnlyList<CompanyBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CompanyBranchDto>>> ListBranches(Guid id, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var exists = await db.Companies
            .AnyAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (!exists) return NotFound();

        var branches = await db.CompanyBranches
            .AsNoTracking()
            .Where(b => b.CompanyId == id)
            .OrderBy(b => b.City).ThenBy(b => b.Source).ThenBy(b => b.Name)
            .Select(b => new CompanyBranchDto(
                b.Id, b.Source, b.ExternalId, b.ExternalUrl, b.Name, b.Address, b.City, b.Rating, b.ReviewCount))
            .ToListAsync(ct);
        return Ok(branches);
    }

    private Guid? GetUserIdOrNull()
    {
        var id = userManager.GetUserId(User);
        return Guid.TryParse(id, out var g) ? g : null;
    }

    private static CompanyDto ToDto(Company c, int branchCount) => new(
        c.Id, c.Name, c.Category, c.Subcategory, c.Cities, c.Description,
        branchCount, c.CreatedAt, c.UpdatedAt);
}
