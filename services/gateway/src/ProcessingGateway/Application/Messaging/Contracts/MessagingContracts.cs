using System.Text.Json.Serialization;

namespace ProcessingGateway.Application.Messaging.Contracts;

/// Команда «запустить анализ» — публикуется Web API (когда появится) либо QA-эндпоинтом
/// `POST /api/qa/analyses` (Bootstrap-ручка, см. IMPLEMENTATION_PLAN.md). Слушает PG.
/// Решение №4 Этапа 0: фиксируем нашу версию контракта; адаптируем при появлении Web API.
/// Бизнес-контекст компании (`BusinessCategory`/`BusinessSubcategory`/`AdditionalContext`) —
/// опциональный снимок из формы нового анализа (Web API читает из `Company` в `webapi_db`).
/// Аддитивное расширение контракта: дефолты null → старые продьюсеры/тесты компилируются и
/// работают без изменений. Доезжает до LLM через `input.json` (см. LlmDispatcher).
public record StartAnalysisCommand(
    Guid AnalysisJobId,
    Guid CompanyId,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    IReadOnlyList<BranchSpec> Branches,
    string? BusinessCategory = null,
    string? BusinessSubcategory = null,
    string? AdditionalContext = null);

public record BranchSpec(
    Guid BranchId,
    string Source,                  // slug: "2gis" | "yandex" | "google" | "otzovik"
    string ExternalId,
    string ExternalUrl);

/// Команда мониторинг-цикла. Семантически = StartAnalysis с `DateFrom = lastCollectedAt`,
/// плюс отдельный финальный event для Notifications в Web API.
public record StartMonitoringCycleCommand(
    Guid AnalysisJobId,
    Guid MonitoringId,
    Guid CompanyId,
    DateTimeOffset LastCollectedAt,
    IReadOnlyList<BranchSpec> Branches);

/// PG публикует когда LLM вернул результат и ингест прошёл. Web API Analytics-модуль
/// подхватывает → считает агрегаты → публикует AggregatesReadyEvent.
public record AnalysisCompletedEvent(
    Guid AnalysisJobId,
    Guid CompanyId,
    AnalysisCompletionStatus Status,
    int ReviewCount);

public enum AnalysisCompletionStatus
{
    /// Pipeline прошёл, ждём агрегаты от Web API. Большая часть jobs.
    CompletedPendingAggregates,

    /// Часть источников упала, но pipeline прошёл по остальным.
    Partial,

    /// LLM вернул failed / reconciliation failed / все источники Parser упали.
    Failed
}

/// Финальный event цикла мониторинга → Web API Notifications-модуль → Telegram.
public record MonitoringCycleCompletedEvent(
    Guid AnalysisJobId,
    Guid MonitoringId,
    Guid CompanyId,
    int NewReviewCount,
    AnalysisCompletionStatus Status);

/// Web API Analytics завершил подсчёт агрегатов → PG финализирует analysis_jobs.status.
public record AggregatesReadyEvent(
    Guid AnalysisJobId,
    Guid CompanyId);

// --- LLM transport (claim-check, ADR-004) ---

/// Публикуется в `Llm__RequestQueue` для внешнего LLM-сервиса.
/// `[JsonPropertyName]` — snake_case на проводе (Python-LLM читает body.message.analysis_job_id),
/// независимо от глобальной MassTransit naming policy.
public record LlmRequestMessage(
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    [property: JsonPropertyName("company_id")] Guid CompanyId,
    [property: JsonPropertyName("payload_url")] string PayloadUrl,           // s3://obratka-jobs/{jobId}/input.json
    [property: JsonPropertyName("review_count")] int ReviewCount,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,     // "2.0"
    [property: JsonPropertyName("callback_queue")] string CallbackQueue);    // "llm.results"

/// LLM публикует в `Llm__ResultQueue` (raw JSON, без MassTransit envelope).
/// PG-consumer для этой очереди настроен на `UseRawJsonDeserializer()`.
/// Schema 2.0: два URL вместо одного (output_reviews.json + output_summary.json).
///
/// `[JsonPropertyName]` — обязательно, иначе snake_case-ключи Python-стороны не мэтчатся
/// с PascalCase-свойствами C# record-а → все поля кроме `Status` (case-insensitive
/// match) становятся null → consumer бросает исключение → infinite retry.
public record LlmResultMessage(
    [property: JsonPropertyName("analysis_job_id")] Guid AnalysisJobId,
    [property: JsonPropertyName("status")] string Status,                    // "finished" | "failed"
    [property: JsonPropertyName("result_reviews_url")] string? ResultReviewsUrl, // при finished
    [property: JsonPropertyName("result_summary_url")] string? ResultSummaryUrl, // при finished
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("error")] string? Error);
