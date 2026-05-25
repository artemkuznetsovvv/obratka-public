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

    // Все компании текущего пользователя — нужно для /history (рядом с jobId показывать имя
     // компании) и для сценариев навигации. Лимита нет — у юзера обычно 1-5 компаний.
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CompanyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CompanyDto>>> ListMine(CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var items = await db.Companies.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new CompanyDto(
                c.Id, c.Name, c.Category, c.Subcategory, c.Cities, c.Description,
                c.Branches.Count, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
        return Ok(items);
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

    // Step 2 submit: пересохраняет всю группировку компании (логические филиалы + привязка
    // карточек). Старые LogicalBranch для этой компании удаляются — соответствующие
    // CompanyBranch.LogicalBranchId сбросятся в null через FK on-delete SetNull. Запрос
    // приходит с фронта целиком — мы не делаем «дельту».
    [HttpPost("{id:guid}/branches/save-groups")]
    [ProducesResponseType(typeof(IReadOnlyList<LogicalBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LogicalBranchDto>>> SaveBranchGroups(
        Guid id, [FromBody] SaveBranchGroupsRequest request, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var company = await db.Companies
            .Include(c => c.Branches)
            .SingleOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (company is null) return NotFound();

        var ownedBranchIds = company.Branches.Select(b => b.Id).ToHashSet();
        var providersInRequest = request.Groups
            .SelectMany(g => g.Providers)
            .Select(p => p.BranchId)
            .ToHashSet();
        var ignored = request.IgnoredBranchIds.ToHashSet();

        // Любой branchId, который пришёл в request, должен быть нашим — иначе юзер
        // фальсифицирует данные. Дублировать карточку между группой и ignored нельзя.
        var allRequested = new HashSet<Guid>(providersInRequest);
        foreach (var ig in ignored) allRequested.Add(ig);
        var unknown = allRequested.Where(b => !ownedBranchIds.Contains(b)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = $"Branch ids do not belong to this company: {string.Join(", ", unknown)}" });

        var duplicates = request.IgnoredBranchIds.Intersect(providersInRequest).ToList();
        if (duplicates.Count > 0)
            return BadRequest(new { error = $"Branch ids appear both in groups and ignored: {string.Join(", ", duplicates)}" });

        // 1. Сносим старые LogicalBranch для этой компании. CompanyBranch.LogicalBranchId
        //    автоматически сбросится в null (SetNull on delete).
        var existingLogical = await db.LogicalBranches
            .Where(lb => lb.CompanyId == id)
            .ToListAsync(ct);
        if (existingLogical.Count > 0)
            db.LogicalBranches.RemoveRange(existingLogical);

        // 2. Создаём новые LogicalBranch'ы и параллельно собираем мапу
        //    CompanyBranch.Id -> (LogicalBranchId, IsEnabled).
        var assignments = new Dictionary<Guid, (Guid? LogicalId, bool IsEnabled)>();
        var newLogicalBranches = new List<LogicalBranch>(request.Groups.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var grp in request.Groups)
        {
            if (grp.Providers.Count == 0)
                return BadRequest(new { error = "Group must contain at least one provider" });

            var logical = new LogicalBranch
            {
                CompanyId = id,
                Name = grp.Name.Trim(),
                Address = grp.Address.Trim(),
                City = grp.City.Trim(),
                IsSelected = grp.IsSelected,
                CreatedAt = now,
                UpdatedAt = now,
            };
            newLogicalBranches.Add(logical);

            foreach (var prov in grp.Providers)
            {
                if (assignments.ContainsKey(prov.BranchId))
                    return BadRequest(new { error = $"Branch id {prov.BranchId} appears in more than one group" });
                assignments[prov.BranchId] = (logical.Id, prov.IsEnabled);
            }
        }

        foreach (var ig in request.IgnoredBranchIds)
            assignments[ig] = (null, false);

        db.LogicalBranches.AddRange(newLogicalBranches);

        // 3. Применяем мапу к карточкам компании. Карточки, которых нет в запросе —
        //    остаются с предыдущим LogicalBranchId (а он удалён, FK SetNull) и
        //    IsSelected = false, т.е. фактически unmatched. Это намеренно: фронт всегда
        //    шлёт ВСЕ карточки текущего поиска в запрос; то что в запрос не попало —
        //    кандидат вне нынешней группировки, не должен влиять на анализ.
        foreach (var branch in company.Branches)
        {
            if (assignments.TryGetValue(branch.Id, out var asn))
            {
                branch.LogicalBranchId = asn.LogicalId;
                branch.IsSelected = asn.IsEnabled;
            }
            else
            {
                // Карточка не упомянута — снимаем привязку и selection. Может произойти,
                // если фронт ничего о ней не знает (например, она от другого города).
                branch.LogicalBranchId = null;
                branch.IsSelected = false;
            }
        }

        company.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Ok(BuildLogicalBranchDtos(newLogicalBranches, company.Branches));
    }

    [HttpGet("{id:guid}/groups")]
    [ProducesResponseType(typeof(IReadOnlyList<LogicalBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LogicalBranchDto>>> ListGroups(Guid id, CancellationToken ct)
    {
        var ownerId = GetUserIdOrNull();
        if (ownerId is null) return Unauthorized();

        var exists = await db.Companies
            .AnyAsync(c => c.Id == id && c.OwnerUserId == ownerId, ct);
        if (!exists) return NotFound();

        var logical = await db.LogicalBranches.AsNoTracking()
            .Where(lb => lb.CompanyId == id)
            .OrderBy(lb => lb.City).ThenBy(lb => lb.Name)
            .ToListAsync(ct);

        var providers = await db.CompanyBranches.AsNoTracking()
            .Where(b => b.CompanyId == id && b.LogicalBranchId != null)
            .ToListAsync(ct);

        return Ok(BuildLogicalBranchDtos(logical, providers));
    }

    private static IReadOnlyList<LogicalBranchDto> BuildLogicalBranchDtos(
        IEnumerable<LogicalBranch> logicalBranches,
        IEnumerable<CompanyBranch> branches)
    {
        var byLogical = branches
            .Where(b => b.LogicalBranchId.HasValue)
            .GroupBy(b => b.LogicalBranchId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return logicalBranches.Select(lb =>
        {
            var cards = byLogical.TryGetValue(lb.Id, out var list)
                ? list.OrderBy(b => b.Source).ToList()
                : new List<CompanyBranch>();

            return new LogicalBranchDto(
                lb.Id, lb.Name, lb.Address, lb.City, lb.IsSelected,
                cards.Select(b => new LogicalBranchProviderDto(
                    b.Id, b.Source, b.ExternalId, b.ExternalUrl, b.Name, b.Address,
                    b.Rating, b.ReviewCount, b.IsSelected)).ToList());
        }).ToList();
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
