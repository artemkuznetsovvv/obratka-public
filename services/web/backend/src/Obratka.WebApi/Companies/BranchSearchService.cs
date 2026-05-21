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
        string Source, string? ExternalId, string? ExternalUrl, string Name, string? Address,
        double? Rating, int? ReviewCount, int? RealReviewsCount);

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
                .Select(r => new RawItem(entry.Source, r.ExternalId, r.ExternalUrl, r.Name, r.Address,
                    r.Rating, r.ReviewCount, r.RealReviewsCount))
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
            logger.LogWarning(ex,
                "Parser-Service returned {Status} for query='{Query}' city='{City}'",
                (int)(ex.StatusCode ?? System.Net.HttpStatusCode.InternalServerError), query, city);
            throw new BranchSearchUnavailableException(
                $"Сервис парсера вернул ошибку {(int)(ex.StatusCode ?? System.Net.HttpStatusCode.InternalServerError)}. Попробуйте ещё раз через минуту.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / DNS / TLS — Parser-Service недоступен на сетевом уровне.
            // Без отдельного catch падало голым 500 от ASP.NET → axios на фронте видел
            // "Request failed with status code 500" вместо понятного текста.
            logger.LogWarning(ex,
                "Parser-Service unreachable for query='{Query}' city='{City}'", query, city);
            throw new BranchSearchUnavailableException(
                "Сервис парсера сейчас недоступен. Проверьте, что стенд поднят, и попробуйте позже.",
                ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // OperationCanceledException БЕЗ внешней отмены = HttpClient.Timeout. Внешнюю отмену
            // (юзер закрыл вкладку, контроллер пробросил ct) не оборачиваем — она дойдёт до
            // ASP.NET как валидный «клиент отвалился».
            logger.LogWarning(ex,
                "Parser-Service timeout for query='{Query}' city='{City}'", query, city);
            throw new BranchSearchUnavailableException(
                "Сервис парсера слишком долго не отвечает. Попробуйте позже.",
                ex);
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
                RealReviewsCount = r.RealReviewsCount,
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
                .Select(r => new RawItem(source, r.ExternalId, r.ExternalUrl, r.Name, r.Address,
                    r.Rating, r.ReviewCount, r.RealReviewsCount))
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
    //  - drops all IsSelected=false rows OF THE CURRENT CITY
    //  - refreshes metadata on rows whose (Source, ExternalId) matches new results
    //  - inserts every remaining found item as a fresh candidate
    //  - SKIPS cross-city duplicates (same (source, externalId) уже привязан к другому
    //    городу этой же компании — иначе нарушим partial unique index, который не
    //    учитывает city). Это типовой сценарий когда парсер на запрос по городу B
    //    возвращает место из города A.
    // Returns groups with the real CompanyBranch.Id for each item — the frontend uses it as the
    // selection key, so we don't need stable externalId from parser plugins.
    private async Task<List<BranchSearchSourceGroup>> PersistCandidatesAsync(
        Guid companyId, string city, List<(string Source, List<RawItem> Items)> rawGroups, CancellationToken ct)
    {
        // Загружаем ВСЕ branches компании (по всем городам) — нужно для cross-city
        // дедупа. Раньше фильтр был b.City == city, и об уже привязанных к другим городам
        // мы ничего не знали → INSERT падал по уникальному индексу.
        var existing = await db.CompanyBranches
            .Where(b => b.CompanyId == companyId)
            .ToListAsync(ct);

        var currentCityCandidates = existing.Where(b => !b.IsSelected && b.City == city).ToList();
        db.CompanyBranches.RemoveRange(currentCityCandidates);

        // Все «оставшиеся» строки (то что мы не удалили) занимают уникальный ключ
        // (CompanyId, Source, ExternalId). При новом INSERT-е мы должны их учитывать,
        // даже если они принадлежат другому городу.
        var availableByExternalKey = existing
            .Except(currentCityCandidates)
            .Where(b => !string.IsNullOrEmpty(b.ExternalId))
            .GroupBy(b => (b.Source, b.ExternalId))
            .ToDictionary(g => g.Key, g => g.First());

        var resultGroups = new List<BranchSearchSourceGroup>(rawGroups.Count);
        var newRows = new List<CompanyBranch>();

        foreach (var (source, items) in rawGroups)
        {
            var resultItems = new List<BranchSearchResultItem>(items.Count);
            // Партиальный уникальный индекс (CompanyId, Source, ExternalId) WHERE ExternalId<>''
            // не переживёт два айтема с одинаковым непустым externalId в одном ответе парсера
            // (бывает на 2ГИС/Яндекс/Google — карточки-дубли). Дедупим в пределах source'а.
            var seenByExternalId = new Dictionary<string, CompanyBranch>(StringComparer.Ordinal);
            foreach (var raw in items)
            {
                var externalId = raw.ExternalId ?? string.Empty;
                if (!string.IsNullOrEmpty(externalId)
                    && seenByExternalId.ContainsKey(externalId))
                {
                    // Дубль из ответа парсера — пропускаем, чтобы не падать на unique key и
                    // не плодить в UI карточки с одинаковым branch.Id.
                    continue;
                }

                CompanyBranch row;
                if (!string.IsNullOrEmpty(externalId)
                    && availableByExternalKey.TryGetValue((source, externalId), out var existingMatch))
                {
                    if (existingMatch.City == city)
                    {
                        // Тот же город — переиспользуем существующую строку (selected или
                        // candidate другой группы) и обновляем метаданные.
                        existingMatch.ExternalUrl = raw.ExternalUrl;
                        existingMatch.Name = raw.Name;
                        existingMatch.Address = raw.Address;
                        existingMatch.Rating = raw.Rating;
                        existingMatch.ReviewCount = raw.ReviewCount;
                        row = existingMatch;
                    }
                    else
                    {
                        // Cross-city: то же (source, externalId) уже привязано к другому городу.
                        // Один external_id = одно физическое место — значит парсер ошибся,
                        // вернул место из другого города. Скипаем, не показываем в этом городе.
                        logger.LogDebug(
                            "Skipping cross-city duplicate: companyId={CompanyId}, source={Source}, externalId={ExternalId}, requested-city={City}, existing-city={ExistingCity}",
                            companyId, source, externalId, city, existingMatch.City);
                        continue;
                    }
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

                if (!string.IsNullOrEmpty(externalId))
                    seenByExternalId[externalId] = row;

                resultItems.Add(new BranchSearchResultItem(
                    row.Id, source, raw.ExternalId, raw.ExternalUrl, raw.Name, raw.Address,
                    raw.Rating, raw.ReviewCount, raw.RealReviewsCount));
            }
            resultGroups.Add(new BranchSearchSourceGroup(source, resultItems));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Если после in-memory dedup всё равно прилетел 23505 — значит между нашим
            // SELECT existing и SaveChanges кто-то параллельно вставил ту же запись
            // (типовой race при одновременных search'ах одной компании). Голым 500 фронту
            // прилетал бы axios "Request failed with status code 500" — это бесполезно
            // юзеру. Лучше — типизированное исключение, контроллер мапит в 502 + текст.
            logger.LogWarning(ex,
                "Race на уникальный индекс company_branches при сохранении кандидатов для companyId={CompanyId}, city={City}",
                companyId, city);
            throw new BranchSearchUnavailableException(
                "Конфликт при сохранении результатов поиска (параллельный запрос). Попробуйте ещё раз.",
                ex);
        }
        return resultGroups;
    }

    /// <summary>
    /// PostgreSQL SQLSTATE 23505 = unique_violation. Используется чтобы отличать
    /// «реальную» проблему от race-конфликтов уникального индекса.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant();
}

/// <summary>
/// Поиск филиалов через Parser-Service провалился по причине, которую имеет смысл
/// показать пользователю текстом, а не как голый 500. Контроллер мапит это в 502
/// + ProblemDetails. Внутренние exception'ы (DB, валидация) сюда НЕ оборачиваем —
/// они должны падать своим path-ом.
/// </summary>
public sealed class BranchSearchUnavailableException : Exception
{
    public BranchSearchUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
