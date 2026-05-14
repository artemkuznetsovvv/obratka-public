using Microsoft.EntityFrameworkCore;
using Obratka.WebApi.Contracts.Companies;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ParserService;
using Obratka.WebApi.Integration.ParserService.Contracts;

namespace Obratka.WebApi.Companies;

internal sealed class BranchSearchService(
    WebApiDbContext db,
    IParserServiceClient parser,
    ILogger<BranchSearchService> logger)
    : IBranchSearchService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private sealed record RawItem(
        string Source, string? ExternalId, string? ExternalUrl, string Name, string? Address, double? Rating, int? ReviewCount);

    public async Task<BranchSearchResponse> SearchAsync(
        Guid companyId, string query, string city, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var queryNorm = Normalize(query);
        var cityNorm = Normalize(city);
        var requestedSources = sources
            .Where(BranchSources.IsKnown)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => s.ToLowerInvariant())
            .ToArray();

        if (requestedSources.Length == 0)
            return new BranchSearchResponse(city, []);

        var now = DateTimeOffset.UtcNow;

        var cached = await db.SearchCache
            .Where(c => c.QueryNormalized == queryNorm
                        && c.CityNormalized == cityNorm
                        && requestedSources.Contains(c.Source)
                        && c.ExpiresAt > now)
            .ToListAsync(ct);

        var rawBySource = new Dictionary<string, List<RawItem>>();
        foreach (var entry in cached)
        {
            rawBySource[entry.Source] = entry.Results
                .Select(r => new RawItem(entry.Source, r.ExternalId, r.ExternalUrl, r.Name, r.Address, r.Rating, r.ReviewCount))
                .ToList();
        }

        var sourcesToFetch = requestedSources.Where(s => !rawBySource.ContainsKey(s)).ToArray();
        if (sourcesToFetch.Length > 0)
        {
            var fresh = await FetchAndCacheAsync(query, city, queryNorm, cityNorm, sourcesToFetch, now, ct);
            foreach (var kvp in fresh) rawBySource[kvp.Key] = kvp.Value;
        }

        // Stable source order: 2gis → yandex → google.
        var orderedSources = BranchSources.All.Where(rawBySource.ContainsKey).ToArray();
        var rawGroups = orderedSources
            .Select(s => (Source: s, Items: rawBySource[s]))
            .ToList();

        var persistedGroups = await PersistCandidatesAsync(companyId, city, rawGroups, ct);
        return new BranchSearchResponse(city, persistedGroups);
    }

    private async Task<Dictionary<string, List<RawItem>>> FetchAndCacheAsync(
        string query, string city, string queryNorm, string cityNorm,
        string[] sourcesToFetch, DateTimeOffset now, CancellationToken ct)
    {
        ParserSearchResponse parserResponse;
        try
        {
            parserResponse = await parser.SearchAsync(
                new ParserSearchRequest(query, city, sourcesToFetch), ct);
        }
        catch (ParserServiceException ex)
        {
            logger.LogWarning(ex, "Parser search failed for query='{Query}' city='{City}'", query, city);
            // Return empty groups so the UI can still show partial results.
            return sourcesToFetch.ToDictionary(s => s, _ => new List<RawItem>());
        }

        var bySource = parserResponse.Results
            .GroupBy(r => r.Source.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var expiresAt = now + CacheTtl;
        var result = new Dictionary<string, List<RawItem>>(sourcesToFetch.Length);

        // Upsert: unique index (QueryNormalized, CityNormalized, Source) is enforced regardless
        // of ExpiresAt — an expired row with the same key still blocks inserts. So we look up
        // first and update in-place instead of always Add()ing.
        var existing = await db.SearchCache
            .Where(c => c.QueryNormalized == queryNorm
                        && c.CityNormalized == cityNorm
                        && sourcesToFetch.Contains(c.Source))
            .ToListAsync(ct);
        var existingBySource = existing.ToDictionary(e => e.Source);

        foreach (var source in sourcesToFetch)
        {
            var items = bySource.TryGetValue(source, out var list) ? list : new List<ParserSearchBranchResult>();
            var resultItems = items.Select(r => new SearchCacheItem
            {
                ExternalId = r.ExternalId ?? string.Empty,
                ExternalUrl = r.ExternalUrl ?? string.Empty,
                Name = r.Name,
                Address = r.Address,
                Rating = r.Rating,
                ReviewCount = r.ReviewCount,
            }).ToList();

            if (existingBySource.TryGetValue(source, out var current))
            {
                current.Results = resultItems;
                current.ExpiresAt = expiresAt;
            }
            else
            {
                db.SearchCache.Add(new SearchCacheEntry
                {
                    QueryNormalized = queryNorm,
                    CityNormalized = cityNorm,
                    Source = source,
                    Results = resultItems,
                    ExpiresAt = expiresAt,
                });
            }

            result[source] = items
                .Select(r => new RawItem(source, r.ExternalId, r.ExternalUrl, r.Name, r.Address, r.Rating, r.ReviewCount))
                .ToList();
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Race с параллельным запросом, который успел вставить ту же запись между нашим
            // SELECT и INSERT. Detach все наши SearchCacheEntry — иначе они останутся в
            // ChangeTracker в Added/Modified и следующий SaveChangesAsync (в PersistCandidatesAsync)
            // снова попытается их применить и снова упадёт на duplicate key.
            logger.LogDebug(ex, "SearchCache upsert race for query='{Query}' city='{City}'", query, city);
            foreach (var entry in db.ChangeTracker.Entries<SearchCacheEntry>().ToList())
                entry.State = EntityState.Detached;
        }

        return result;
    }

    // Re-syncs candidate branches in `company_branches` for (companyId, city):
    //  - drops all IsSelected=false rows
    //  - refreshes metadata on IsSelected=true rows whose (Source, ExternalId) matches new results (non-empty externalId only)
    //  - inserts every remaining found item as a fresh candidate
    // Returns groups with the real CompanyBranch.Id for each item — the frontend uses it as the
    // selection key, so we don't need stable externalId from parser plugins.
    private async Task<List<BranchSearchSourceGroup>> PersistCandidatesAsync(
        Guid companyId, string city, List<(string Source, List<RawItem> Items)> rawGroups, CancellationToken ct)
    {
        var existing = await db.CompanyBranches
            .Where(b => b.CompanyId == companyId && b.City == city)
            .ToListAsync(ct);

        var selected = existing.Where(b => b.IsSelected).ToList();
        var candidates = existing.Where(b => !b.IsSelected).ToList();
        db.CompanyBranches.RemoveRange(candidates);

        var selectedByExternalKey = selected
            .Where(b => !string.IsNullOrEmpty(b.ExternalId))
            .GroupBy(b => (b.Source, b.ExternalId))
            .ToDictionary(g => g.Key, g => g.First());

        var resultGroups = new List<BranchSearchSourceGroup>(rawGroups.Count);
        var newRows = new List<CompanyBranch>();

        foreach (var (source, items) in rawGroups)
        {
            var resultItems = new List<BranchSearchResultItem>(items.Count);
            foreach (var raw in items)
            {
                var externalId = raw.ExternalId ?? string.Empty;
                CompanyBranch row;
                if (!string.IsNullOrEmpty(externalId)
                    && selectedByExternalKey.TryGetValue((source, externalId), out var existingSelected))
                {
                    existingSelected.ExternalUrl = raw.ExternalUrl;
                    existingSelected.Name = raw.Name;
                    existingSelected.Address = raw.Address;
                    existingSelected.Rating = raw.Rating;
                    existingSelected.ReviewCount = raw.ReviewCount;
                    row = existingSelected;
                }
                else
                {
                    row = new CompanyBranch
                    {
                        CompanyId = companyId,
                        Source = source,
                        ExternalId = externalId,
                        ExternalUrl = raw.ExternalUrl,
                        Name = raw.Name,
                        Address = raw.Address,
                        City = city,
                        Rating = raw.Rating,
                        ReviewCount = raw.ReviewCount,
                        IsSelected = false,
                    };
                    db.CompanyBranches.Add(row);
                    newRows.Add(row);
                }
                resultItems.Add(new BranchSearchResultItem(
                    row.Id, source, raw.ExternalId, raw.ExternalUrl, raw.Name, raw.Address, raw.Rating, raw.ReviewCount));
            }
            resultGroups.Add(new BranchSearchSourceGroup(source, resultItems));
        }

        await db.SaveChangesAsync(ct);
        return resultGroups;
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant();
}
