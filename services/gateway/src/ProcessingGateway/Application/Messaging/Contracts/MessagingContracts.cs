namespace ProcessingGateway.Application.Messaging.Contracts;

/// Команда «запустить анализ» — публикуется Web API (когда появится) либо QA-эндпоинтом
/// `POST /api/qa/analyses` (Bootstrap-ручка, см. IMPLEMENTATION_PLAN.md). Слушает PG.
/// Решение №4 Этапа 0: фиксируем нашу версию контракта; адаптируем при появлении Web API.
public record StartAnalysisCommand(
    Guid AnalysisJobId,
    Guid CompanyId,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    IReadOnlyList<BranchSpec> Branches);

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
public record LlmRequestMessage(
    Guid AnalysisJobId,
    Guid CompanyId,
    string PayloadUrl,                  // s3://obratka-jobs/{jobId}/input.json
    int ReviewCount,
    string SchemaVersion,               // "2.0"
    string CallbackQueue);              // "llm.results"

/// LLM публикует в `Llm__ResultQueue` (raw JSON, без MassTransit envelope).
/// PG-consumer для этой очереди настроен на `UseRawJsonDeserializer()`.
/// Schema 2.0: два URL вместо одного (output_reviews.json + output_summary.json).
public record LlmResultMessage(
    Guid AnalysisJobId,
    string Status,                      // "finished" | "failed"
    string? ResultReviewsUrl,           // s3://obratka-jobs/{jobId}/output_reviews.json — при finished
    string? ResultSummaryUrl,           // s3://obratka-jobs/{jobId}/output_summary.json — при finished
    string SchemaVersion,
    string? Error);
