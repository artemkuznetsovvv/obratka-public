using ProcessingGateway.Domain;

namespace ProcessingGateway.Application.Ingestion;

/// `RawReviewDto` (вытащенный из `raw/{source}.json`) → `Review` (POCO для INSERT).
/// `Id` не проставляется — БД сама даст identity. `NormalizedText` остаётся null
/// (это зона LLM, см. CLAUDE.md). `CollectedAt` берём из `payload.CollectedAt` —
/// это момент завершения сбора Parser-ом, что точнее DEFAULT NOW().
public static class RawReviewMapper
{
    /// `Stars` в источниках всегда int 1..5 (Parser RawReview.Stars не nullable).
    /// В БД `stars` SMALLINT nullable — приводим к short.
    public static Review ToEntity(CollectionResultPayload payload, RawReviewDto dto) => new()
    {
        CompanyId = payload.CompanyId,
        BranchId = dto.BranchId,
        Source = payload.Source,
        ExternalId = dto.ExternalId,
        CompositeKey = CompositeKeyBuilder.Build(
            payload.Source, dto.BranchId, dto.ExternalId, dto.Date, dto.Text),
        RawText = dto.Text,
        NormalizedText = null,
        TextLanguage = dto.TextLanguage,
        ReviewDate = dto.Date,
        Stars = (short)dto.Stars,
        AuthorName = dto.AuthorName,
        AuthorPublicId = dto.AuthorPublicId,
        CollectedAt = payload.CollectedAt
    };
}
