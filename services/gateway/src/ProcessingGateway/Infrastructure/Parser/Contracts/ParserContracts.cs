namespace ProcessingGateway.Infrastructure.Parser.Contracts;

/// HTTP-контракты Parser-а — копия `Parser-Service/Api/Contracts/*`. JSON snake_case
/// сериализация настроена в `ParserHttpClient` через `JsonSerializerOptions`.
/// Сюда включены **только** те поля, которые PG отправляет/принимает; QA-эндпоинты
/// и остальные части Parser API за рамками.
///
/// PG обращается к двум эндпоинтам (ADR-001 §3-5):
///   POST /api/collection-tasks       → запуск сбора (один task на источник)
///   GET  /api/collection-tasks/{id}  → polling статуса (3-5 сек)

public record StartCollectionRequest(
    Guid JobId,
    Guid CompanyId,
    string Source,                                        // slug: "2gis" | "yandex" | "google" | "otzovik"
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    IReadOnlyList<BranchTargetDto> Branches);

public record BranchTargetDto(
    Guid BranchId,
    string ExternalId,
    string ExternalUrl);

public record StartCollectionResponse(Guid TaskId);

public record CollectionTaskStatusResponse(
    Guid TaskId,
    string Status,                                        // "pending" | "running" | "completed" | "failed"
    string Source,
    double Progress,                                      // 0..1 (Parser возвращает дробью; PG для UI масштабирует к 0..100)
    int? ReviewCount,
    string? S3Url,
    string? Error);
