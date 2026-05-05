namespace ProcessingGateway.Application.Ingestion;

/// Контракт `s3://obratka-jobs/{jobId}/raw/{source}.json` — то, что пишет Parser
/// в `S3ResultStorage.UploadResultAsync`. Поля и порядок — копия Parser-овского
/// `CollectionResult`/`RawReview`. JSON snake_case (`PropertyNamingPolicy = SnakeCaseLower`),
/// `date` приходит как ISO 8601 c TZ offset (`2026-03-30T10:11:55.113+00:00`).
public record CollectionResultPayload(
    Guid TaskId,
    Guid JobId,
    string Source,                    // slug: "2gis" | "yandex" | "google" | "otzovik"
    Guid CompanyId,
    DateTimeOffset CollectedAt,
    IReadOnlyList<RawReviewDto> Reviews);

public record RawReviewDto(
    string ExternalId,
    string Text,
    DateTimeOffset Date,
    int Stars,
    Guid BranchId,
    string? AuthorName = null,
    string? AuthorPublicId = null,
    string? TextLanguage = null);
