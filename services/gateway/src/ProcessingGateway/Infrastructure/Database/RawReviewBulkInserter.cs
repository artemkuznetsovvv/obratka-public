using Dapper;
using ProcessingGateway.Domain;

namespace ProcessingGateway.Infrastructure.Database;

/// Bulk-INSERT отзывов из `raw/{source}.json` в `reviews`. Дедуп через UNIQUE
/// `composite_key` + `ON CONFLICT DO NOTHING`. Один SQL-запрос на батч (UNNEST с
/// массивами параметров) — на 1000 строк укладывается в ~50-100 мс против ~500 мс,
/// если бы Dapper делал row-by-row INSERT-ы.
///
/// Возвращает число вставленных строк (= уникальных по composite_key из батча,
/// которых ещё не было в БД).
public sealed class RawReviewBulkInserter
{
    private readonly IDbConnectionFactory _factory;

    public RawReviewBulkInserter(IDbConnectionFactory factory) => _factory = factory;

    private const string Sql = @"
        INSERT INTO reviews
            (company_id, branch_id, source, external_id, composite_key,
             raw_text, text_language, review_date, stars,
             author_name, author_public_id, collected_at)
        SELECT
            company_id, branch_id, source, external_id, composite_key,
            raw_text, text_language, review_date, stars::smallint,
            author_name, author_public_id, collected_at
        FROM UNNEST(
            @CompanyIds, @BranchIds, @Sources, @ExternalIds, @CompositeKeys,
            @RawTexts, @TextLanguages, @ReviewDates, @StarsList,
            @AuthorNames, @AuthorPublicIds, @CollectedAts
        ) AS t(
            company_id, branch_id, source, external_id, composite_key,
            raw_text, text_language, review_date, stars,
            author_name, author_public_id, collected_at
        )
        ON CONFLICT (composite_key) DO NOTHING;";

    public async Task<int> InsertAsync(IReadOnlyCollection<Review> reviews, CancellationToken ct = default)
    {
        if (reviews.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);

        // Postgres `timestamptz` через Npgsql ожидает UTC DateTime в массиве (Npgsql 6+).
        // DateTimeOffset → ToUniversalTime → DateTime со Specified=Utc.
        var parameters = new
        {
            CompanyIds       = reviews.Select(r => r.CompanyId).ToArray(),
            BranchIds        = reviews.Select(r => r.BranchId).ToArray(),
            Sources          = reviews.Select(r => r.Source).ToArray(),
            ExternalIds      = reviews.Select(r => r.ExternalId).ToArray(),
            CompositeKeys    = reviews.Select(r => r.CompositeKey).ToArray(),
            RawTexts         = reviews.Select(r => r.RawText).ToArray(),
            TextLanguages    = reviews.Select(r => r.TextLanguage).ToArray(),
            ReviewDates      = reviews.Select(r => r.ReviewDate.UtcDateTime).ToArray(),
            StarsList        = reviews.Select(r => (int?)r.Stars).ToArray(),
            AuthorNames      = reviews.Select(r => r.AuthorName).ToArray(),
            AuthorPublicIds  = reviews.Select(r => r.AuthorPublicId).ToArray(),
            CollectedAts     = reviews.Select(r => r.CollectedAt.UtcDateTime).ToArray()
        };

        return await conn.ExecuteAsync(new CommandDefinition(Sql, parameters, cancellationToken: ct));
    }
}
