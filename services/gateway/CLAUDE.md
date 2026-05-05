# Processing Gateway

Оркестратор pipeline анализа отзывов: получает команды от Web API → запускает сбор в Parser
Service → сохраняет сырые отзывы в `processing_db` → публикует claim-check во внешний LLM →
сохраняет результаты → уведомляет Web API. **Единственная точка интеграции с внешним LLM**.

> Источники истины:
> - `../ADR-obratka-app/.../overview.md` — общий контекст системы.
> - ADR-001 (Parser ↔ PG), ADR-002 (схема `processing_db`), ADR-004 (LLM-транспорт), ADR-008 (логи), ADR-011 (декомпозиция, отдельные БД).
> - `../Parser-Service/CLAUDE.md` + `../Parser-Service/docs/flow-parser.md` — контракт Parser, который PG **обязан** соблюдать.

При расхождении между этим файлом и ADR — побеждает ADR.

---

## Стек

| Слой | Выбор | Источник |
|------|-------|----------|
| Runtime | C# / ASP.NET Core (.NET 9 — версия Parser-а, выбрана из-за совместимости Playwright) | — |
| ORM | EF Core + Npgsql (схема, миграции, single-row CRUD); Dapper (bulk INSERT в `reviews` и `review_llm_results`) | ADR-002 |
| БД | PostgreSQL `processing_db` (отдельный инстанс, не общий с Web API) | ADR-002, ADR-011 |
| Брокер | MassTransit + RabbitMQ | ADR-004 |
| Blob | AWSSDK.S3 → MinIO bucket `obratka-jobs` (тот же, что у Parser) | ADR-001, ADR-004 |
| HTTP-клиент | `HttpClient` через `IHttpClientFactory` (named client `parser`) | — |
| Outbox | `MassTransit.EntityFrameworkCoreIntegration` Outbox — атомарность БД↔брокер | ADR-004 (соблюсти exactly-once-ish) |
| Логи | Serilog → Seq, обязательный enricher `Service = "ProcessingGateway"` | ADR-008 |
| Тесты | xUnit + WebApplicationFactory; Testcontainers для Postgres/RabbitMQ/MinIO | — |

ASP.NET Core нужен только для одного публичного эндпоинта `GET /api/analyses/{jobId}/status` —
HTTP-сервер не несёт BFF-логику. Никаких прямых вызовов от Frontend: с UI говорит **только** Web API.

---

## Команды (план)

```bash
dotnet run --project src/ProcessingGateway          # Запуск
dotnet test                                          # Unit + integration
docker compose up --build                            # Локально: PG + Postgres + RabbitMQ + MinIO + Seq
```

`docker-compose.yml` поднимает зависимости + сам сервис. MinIO bucket `obratka-jobs` создаётся
init-контейнером (как в Parser); RabbitMQ — `rabbitmq:3-management`; Seq — `datalust/seq`.

---

## Структура проекта

```
ProcessingGateway/
├── Api/
│   └── AnalysisStatusController.cs       ← GET /api/analyses/{jobId}/status
│
├── Application/
│   ├── Pipeline/
│   │   ├── AnalysisOrchestrator.cs       ← FSM: переходы analysis_jobs.status
│   │   ├── ParserPoller.cs               ← опрос Parser, агрегация collection_progress
│   │   ├── LlmDispatcher.cs              ← upload input.json, publish LLM request
│   │   └── LlmResultIngestor.cs          ← consume LLM ответ, save review_llm_results
│   ├── Consumers/
│   │   ├── StartAnalysisCommandConsumer.cs
│   │   ├── StartMonitoringCycleCommandConsumer.cs
│   │   ├── AggregatesReadyEventConsumer.cs       ← финализирует analysis_jobs.status
│   │   └── LlmResultMessageConsumer.cs           ← внешний контракт LLM
│   └── Reconciliation/
│       └── LlmStatusReconciler.cs        ← REST fallback к LLM при таймауте
│
├── Domain/
│   ├── AnalysisJob.cs
│   ├── Review.cs
│   ├── ReviewLlmResult.cs
│   └── AnalysisJobStatus.cs              ← enum со снапшотом строк ADR-004
│
├── Infrastructure/
│   ├── Database/
│   │   ├── ProcessingDbContext.cs
│   │   └── Migrations/
│   ├── Parser/
│   │   ├── IParserClient.cs
│   │   └── ParserHttpClient.cs           ← обёртка над POST/GET /api/collection-tasks
│   ├── Storage/
│   │   ├── IJobBlobStorage.cs            ← read raw/{source}.json, write input.json, read output.json
│   │   └── S3JobBlobStorage.cs
│   ├── Messaging/
│   │   ├── BrokerEndpoints.cs            ← имена очередей/exchange
│   │   └── Contracts/                    ← record-сообщения MassTransit
│   └── Telemetry/
│       └── CorrelationIdMiddleware.cs
│
└── Program.cs
```

Парсер-плагины и Playwright **не подключаются** — это зона Parser Service.

---

## Конфигурация (ENV)

| Переменная | Пример | Назначение |
|------------|--------|-----------|
| `ConnectionStrings__ProcessingDb` | `Host=processing-db;Database=processing;Username=processing_user;Password=...` | основная БД |
| `Parser__BaseUrl` | `http://parser-service:8080` (или VPS-URL `https://parser.193.233.217.223.sslip.io`) | базовый URL Parser HTTP API |
| `Parser__PollIntervalSeconds` | `4` | интервал опроса task; ADR-001 — 3–5 |
| `Parser__TaskTimeoutMinutes` | `90` | максимальное время задачи Parser (Playwright 15–60 мин + запас) |
| `S3__Endpoint` | `http://minio:9000` | MinIO endpoint |
| `S3__AccessKey` / `S3__SecretKey` | `minioadmin` / `minioadmin` | в проде — IAM-ключи с least privilege |
| `S3__BucketName` | `obratka-jobs` | **обязательно совпадает с Parser** |
| `RabbitMq__Host` | `rabbitmq` | брокер |
| `Llm__RequestQueue` | `llm.requests` | очередь публикации задач LLM |
| `Llm__ResultQueue` | `llm.results` | очередь, на которую подписан PG (`callback_queue` из сообщения) |
| `Llm__StatusBaseUrl` | `https://llm.internal/status` | reconciliation REST `/status/{jobId}` |
| `Llm__ResultTimeoutMinutes` | `30` | если ответа нет → reconciliation |
| `Seq__Url` | `http://seq:5341` | централизованные логи (ADR-008) |

`processing_user` и `analytics_reader` — разные роли Postgres. PG пишет полным набором прав;
Web API подключается отдельным пользователем `analytics_reader` с `SELECT` на 3 таблицы (ADR-011).

---

## Контракт с Parser Service (HTTP)

Базовый путь Parser — `/api/collection-tasks` (см. `../Parser-Service/CLAUDE.md` и `../Parser-Service/docs/flow-parser.md`). PG **не вызывает** `/qa/*` и `/search` (поиск делает Web API).

### Старт сбора (один task = один источник, ADR-001 §4)

```
POST {Parser__BaseUrl}/api/collection-tasks
Headers: X-Correlation-ID: <correlationId>
Body:
{
  "jobId":     "<analysis_job_id>",
  "companyId": "<company_id>",
  "source":    "yandex",                          // slug: 2gis | yandex | google | otzovik
  "dateFrom":  "2025-01-01T00:00:00Z",            // ISO 8601 + Z, опционально
  "dateTo":    "2025-04-01T00:00:00Z",            // опционально, default — Parser проставит UtcNow
  "branches": [
    { "branchId":"<uuid>", "externalId":"1124715036", "externalUrl":"https://yandex.ru/maps/org/.../1124715036/" }
  ]
}
→ 202 Accepted
→ { "taskId": "<uuid>" }
```

Источники запускаются **параллельно**: один POST на источник, общий `jobId`. PG сохраняет
`taskId` per source в `analysis_jobs.collection_progress`.

### Polling статуса (ADR-001 §5)

```
GET {Parser__BaseUrl}/api/collection-tasks/{taskId}
→ {
    "taskId":      "<uuid>",
    "status":      "running" | "pending" | "completed" | "failed",
    "source":      "yandex",
    "progress":    0.6,                            // 0..1
    "reviewCount": null,                           // заполнен после completed
    "s3Url":       null,                           // s3://obratka-jobs/{jobId}/raw/{source}.json после completed
    "error":       null
  }
```

Интервал — `Parser__PollIntervalSeconds` (3–5 с). Polling выполняется на стороне PG;
Parser **не публикует** broker-событий завершения (ADR-001 §5, явно).

### Скачивание результата

При `status == completed` PG читает `s3Url` напрямую из MinIO (тот же bucket, что и Parser).
Структура файла — ADR-001 §6, формат фактически — `CollectionResult` из Parser-кода:

```json
{
  "task_id": "...",
  "job_id": "...",
  "source": "yandex",                              // slug
  "company_id": "...",
  "collected_at": "2024-03-15T14:22:00Z",
  "reviews": [
    {
      "external_id": "abc123",
      "text": "...",
      "date": "2024-03-10T09:00:00Z",
      "stars": 5,
      "branch_id": "<uuid>",
      "author_name": null,
      "author_public_id": null,
      "text_language": null
    }
  ]
}
```

**Важно:** snake_case (`PropertyNamingPolicy = SnakeCaseLower`), ровно как пишет Parser
(`S3ResultStorage.cs`). Десериализатор PG обязан использовать ту же политику.

### Изоляция сбоев (ADR-001 §10)

Сбой одного источника **не валит весь job**. Если 2gis = `failed`, а yandex/google = `completed` —
PG продолжает pipeline по успешным источникам и помечает финальный статус job-а как `partial`
(см. статусную машину ниже).

---

## Схема БД (`processing_db`)

Создаётся миграцией EF Core, точное соответствие ADR-002/ADR-004.

```sql
CREATE TABLE reviews (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    branch_id       UUID NOT NULL,
    source          VARCHAR(50) NOT NULL,           -- '2gis' | 'yandex' | 'google' | 'otzovik'
    external_id     VARCHAR(500),
    composite_key   VARCHAR(1000) NOT NULL UNIQUE,  -- source + branch_id + date + normalized(text)
    raw_text        TEXT NOT NULL,
    normalized_text TEXT,
    text_language   VARCHAR(10),                    -- из Parser RawReview.TextLanguage, опц.
    review_date     TIMESTAMPTZ NOT NULL,
    stars           SMALLINT,
    author_name     TEXT,                           -- из Parser RawReview.AuthorName, опц.
    author_public_id TEXT,                          -- из Parser RawReview.AuthorPublicId, опц.
    collected_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE UNIQUE INDEX idx_reviews_external
    ON reviews (source, branch_id, external_id) WHERE external_id IS NOT NULL;
CREATE INDEX idx_reviews_company_date ON reviews (company_id, review_date DESC);

CREATE TABLE review_llm_results (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    review_id            UUID NOT NULL REFERENCES reviews(id),
    analysis_job_id      UUID NOT NULL,
    fake_status          VARCHAR(20) NOT NULL,
    fake_reason_tags     JSONB NOT NULL DEFAULT '[]',
    sentiment            VARCHAR(20),
    sentiment_confidence FLOAT,
    is_spam              BOOLEAN NOT NULL,
    spam_confidence      FLOAT NOT NULL,
    topics               JSONB NOT NULL DEFAULT '[]',
    processed_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (review_id, analysis_job_id)
);
CREATE INDEX idx_llm_results_job    ON review_llm_results (analysis_job_id);
CREATE INDEX idx_llm_results_topics ON review_llm_results USING GIN (topics);

CREATE TABLE analysis_jobs (
    id                   UUID PRIMARY KEY,
    company_id           UUID NOT NULL,
    status               VARCHAR(40) NOT NULL,
    review_count         INT NOT NULL DEFAULT 0,
    collection_progress  JSONB NOT NULL DEFAULT '{}',  -- { "yandex": {"task_id":"...","status":"running","progress":60} }
    payload_url          TEXT,                          -- s3://.../input.json
    result_url           TEXT,                          -- s3://.../output.json
    recommendation       TEXT,                          -- job-level (ADR-004 §2)
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at              TIMESTAMPTZ,
    completed_at         TIMESTAMPTZ,
    error                TEXT
);
```

`recommendation` хранится здесь, а **не** в `review_llm_results` — это строка на уровне job.

### Дедупликация при вставке

```csharp
// Используется при ингесте raw/{source}.json от Parser
INSERT INTO reviews (...) VALUES (...)
ON CONFLICT (composite_key) DO NOTHING;
```

`composite_key` формирует PG (детерминированно), а не Parser — у Parser нет понятия `normalized_text`.

### Владение

| Таблица | Пишет | Читает |
|---------|-------|--------|
| `reviews`, `review_llm_results`, `analysis_jobs` | **только** Processing Gateway | Web API Analytics-модуль (`analytics_reader`, read-only, ADR-011) |

В чужие таблицы PG **не пишет** и не читает.

---

## Статусная машина `analysis_jobs.status`

Отклонение от ADR-004 §4: стадия `language_detection` **исключена**. Язык отзыва приходит из
Parser в поле `RawReview.TextLanguage` (когда плагин его извлекает) и сохраняется в
`reviews.text_language`; отдельный шаг pipeline для этого не нужен. LLM сам решает,
что делать с языком и/или нормализацией.

```
pending
   → collecting             (первый POST /collection-tasks отправлен в Parser)
   → sent_to_llm            (все Parser-таски в completed/failed; есть ≥1 успешный источник;
                             input.json загружен, LLM-сообщение опубликовано)
   → computing_aggregates   (LLM вернул output.json, он распарсен и сохранён,
                             AnalysisCompletedEvent опубликован)
   → completed              (получен AggregatesReadyEvent от Web API)
   → partial                (≥1 источник failed, но pipeline прошёл по успешным)
   → failed                 (все источники failed / LLM вернул failed / reconciliation провалился)
```

PG **владеет** этим полем — только PG обновляет `status`. Финальный переход
`computing_aggregates → completed/partial` инициируется приходом `AggregatesReadyEvent` из Web API
(ADR-011 §«Коммуникация модулей внутри Web API»). Frontend stages (см. HTTP Status API
ниже) тоже больше не содержат `language_detection`.

---

## HTTP Status API (для Web API → Frontend progress screen)

```
GET /api/analyses/{jobId}/status
→ 200 {
    "status": "collecting",                                  // одно из значений ФСМ
    "stages": [
      { "key": "collecting",         "label": "Сбор отзывов",                       "state": "active" },
      { "key": "llm_analysis",       "label": "Анализ (фейки, тональность, темы)",  "state": "pending" },
      { "key": "building_dashboard", "label": "Построение дашборда",                "state": "pending" }
    ],
    "sources": {
      "2gis":   { "status": "running",   "progress": 60 },
      "yandex": { "status": "completed", "review_count": 143 }
    },
    "review_count": 143
  }
→ 404 если job не найден
```

`state` ∈ `pending | active | completed | failed`. Поле `sources` — рендер JSONB
`collection_progress` (per source: `taskId`, `status`, `progress` 0–100, `reviewCount`).

Эндпоинт — **внутренний**, наружу публикуется только через Web API (BFF). Доступ к нему
извне docker-сети закрыт (ADR-overview, секция Frontend SPA).

---

## Контракт с внешним LLM (ADR-004)

LLM — чёрный ящик, наш контракт. Транспорт — RabbitMQ + claim-check через MinIO.

### `s3://obratka-jobs/{jobId}/input.json` (мы пишем, LLM читает)

```json
{
  "schema_version": "1.0",
  "analysis_job_id": "<uuid>",
  "company_id": "<uuid>",
  "reviews": [
    { "review_id": "<uuid>", "text": "...", "source": "2gis", "date": "...", "stars": 5, "branch_id": "<uuid>" }
  ]
}
```

`review_id` — это `reviews.id` из нашей БД, без него LLM не сможет связать `processedReview` обратно.

### Сообщение LLM (мы публикуем в `Llm__RequestQueue`)

```json
{
  "analysis_job_id": "<uuid>",
  "company_id": "<uuid>",
  "payload_url": "s3://obratka-jobs/<jobId>/input.json",
  "review_count": 847,
  "schema_version": "1.0",
  "callback_queue": "llm.results"
}
```

### Сообщение от LLM (PG подписан на `Llm__ResultQueue`)

```json
{
  "analysis_job_id": "<uuid>",
  "status": "finished",
  "result_url": "s3://obratka-jobs/<jobId>/output.json",
  "schema_version": "1.0"
}
```

При `status == failed` — `result_url` отсутствует, есть `"error"`.

### `s3://obratka-jobs/{jobId}/output.json` (LLM пишет, мы читаем)

```json
{
  "schema_version": "1.0",
  "analysis_job_id": "<uuid>",
  "recommendation": "Улучшить скорость обслуживания...",
  "processedReview": [
    {
      "review_id": "<uuid>",
      "fake_status": "normal",
      "fake_reason_tags": [],
      "sentiment": "positive",
      "sentiment_confidence": 0.92,
      "is_spam": false,
      "spam_confidence": 0.04,
      "topics": ["персонал", "качество еды"]
    }
  ]
}
```

Маппинг:
- `recommendation` → `analysis_jobs.recommendation`.
- каждый элемент `processedReview` → строка `review_llm_results` (UNIQUE `(review_id, analysis_job_id)`,
  `ON CONFLICT DO NOTHING` для идемпотентности повторного consume).

### Reconciliation (ADR-004 §4)

Если ответа нет за `Llm__ResultTimeoutMinutes` — PG поллит `Llm__StatusBaseUrl/{jobId}`
(REST). Возможные ответы LLM: `processing` / `finished` / `failed`. На `finished` — читаем
указанный `result_url` и проходим обычный путь ингеста; на `failed` → `failed` + Telegram-алерт
через `AnalysisCompletedEvent { status = failed }`.

---

## Шина: MassTransit / RabbitMQ

### Подписки (consumers)

| Сообщение | От кого | Что делает |
|-----------|---------|-----------|
| `StartAnalysisCommand { analysisJobId, companyId, branches[], dateFrom?, dateTo? }` | Web API | создаёт `analysis_jobs(pending)`, переводит в `collecting`, запускает Parser-таски + polling |
| `StartMonitoringCycleCommand { monitoringId, companyId, branches[], lastCollectedAt }` | Web API (Hangfire) | то же, но `dateFrom = lastCollectedAt`; в конце публикует `MonitoringCycleCompletedEvent` |
| `AggregatesReadyEvent { analysisJobId, companyId }` | Web API (Analytics) | финализирует `analysis_jobs.status = completed/partial`, проставляет `completed_at` |
| LLM result (см. выше) | LLM | ingest output.json |

### Публикации (producers)

| Сообщение | Когда | Кто слушает |
|-----------|-------|-------------|
| LLM request (см. выше) | после загрузки input.json | внешний LLM |
| `AnalysisCompletedEvent { analysisJobId, companyId, status, reviewCount }` | LLM-результат сохранён в БД | Web API → запускает Analytics + Notifications |
| `MonitoringCycleCompletedEvent { monitoringId, companyId, newReviewCount, status }` | завершение цикла мониторинга | Web API → Notifications |

`status` в `AnalysisCompletedEvent` принимает `completed_pending_aggregates | partial | failed`
— это **операционный** статус PG; финальный `completed/partial` проставляется уже в БД при
получении `AggregatesReadyEvent`.

### MassTransit-конфигурация (важное)

- `MessageRetry.Exponential(...)` + `UseDelayedRedelivery` — на transient-сбои.
- `_error` / `_skipped` очереди настроены MassTransit'ом по умолчанию → роняем туда сообщения
  при исчерпании retry, оттуда — алерт админу (через Web API Notifications).
- `CorrelationId` берём из envelope (если пришло) либо генерируем; **обязательно** прокидываем
  как заголовок `X-Correlation-ID` во все исходящие HTTP-вызовы Parser-а.

---

## Сквозной сценарий разового анализа (ADR-001 §7)

```
Web API publish StartAnalysisCommand
  └── PG StartAnalysisCommandConsumer
        ├── INSERT analysis_jobs(status='pending', collection_progress={})
        ├── status → 'collecting'
        ├── ParserHttpClient.StartCollection per source  (3 POST /collection-tasks параллельно)
        │     └── update analysis_jobs.collection_progress[source] = { task_id, status:'pending', progress:0 }
        └── ParserPoller.StartLoop(jobId)

ParserPoller (каждые 3–5 сек):
  ├── GET /collection-tasks/{taskId} per active source
  ├── обновляет collection_progress[source]
  ├── при completed: скачивает s3Url → парсит CollectionResult →
  │     INSERT INTO reviews ON CONFLICT (composite_key) DO NOTHING
  ├── при failed: фиксирует error в collection_progress[source]
  └── когда нет running/pending источников →
        AnalysisOrchestrator.AdvanceAfterCollection(jobId)

AnalysisOrchestrator.AdvanceAfterCollection:
  ├── если все источники failed → status='failed', publish AnalysisCompletedEvent(failed)
  ├── иначе:
  │     status → 'sent_to_llm'
  │     LlmDispatcher.DispatchAsync(jobId):
  │       ├── собрать input.json из reviews этого job-а
  │       ├── PUT s3://obratka-jobs/{jobId}/input.json
  │       ├── analysis_jobs.payload_url = ..., sent_at = NOW()
  │       └── publish LLM request в Llm__RequestQueue

LlmResultMessageConsumer (Llm__ResultQueue):
  ├── при status='failed' → status='failed', publish AnalysisCompletedEvent(failed)
  └── при status='finished':
        ├── скачать output.json
        ├── analysis_jobs.recommendation = output.recommendation
        ├── BULK INSERT review_llm_results ON CONFLICT (review_id, analysis_job_id) DO NOTHING
        ├── status → 'computing_aggregates'
        ├── analysis_jobs.result_url = ...
        └── publish AnalysisCompletedEvent(completed_pending_aggregates | partial)

AggregatesReadyEventConsumer (от Web API):
  └── status → 'completed' (или 'partial' если на этапе сбора были failed),
      analysis_jobs.completed_at = NOW()
```

Live-мониторинг идёт по тому же пути, но с `dateFrom = lastCollectedAt` и финальной публикацией
`MonitoringCycleCompletedEvent`.

---

## Логирование и Correlation ID (ADR-008)

- Serilog → Seq, sink `WriteTo.Seq(Seq__Url)`.
- Enricher: `Service = "ProcessingGateway"`, `MachineName`.
- HTTP middleware читает `X-Correlation-ID`, иначе генерирует Guid.ToString("N");
  кладёт в `LogContext` и отдаёт обратно в response header.
- Каждый исходящий вызов `ParserHttpClient` добавляет `X-Correlation-ID` (DelegatingHandler).
- MassTransit `CorrelationId` envelope ↔ Serilog `LogContext.PushProperty("CorrelationId", ...)`
  в каждом consumer'е.
- В каждом consumer'е также пушим `AnalysisJobId`, `CompanyId` — для трейсинга через UI Seq.
- `Microsoft.AspNetCore` и `System.Net.Http` — на `Warning` (как в ADR-008 §template).

Ключевые события (ADR-008 §«Что и как логируется»):
- Information: задача Parser создана, отзывы сохранены (`count`), LLM pipeline запущен,
  output.json применён, status-переход.
- Warning: ошибка источника (transient retry), LLM reconciliation сработал.
- Error: превышен retry limit, LLM вернул failed, reconciliation провалился, S3 недоступен.

`Error`/`Fatal` дополнительно идут в Telegram-алерт админу через Web API Notifications
(подписан на эти события в Seq Signal или ловит через broker — решается при реализации).

---

## Обработка сбоев (ADR-004 §5)

| Сценарий | Поведение PG |
|----------|-------------|
| Parser вернул `failed` для одного источника | фиксируем `collection_progress[source].error`, продолжаем по остальным; финальный `partial` |
| Parser-таск зависает > `Parser__TaskTimeoutMinutes` | помечаем источник `failed(reason=timeout)`, изоляция как выше |
| S3 недоступен на upload `input.json` | MassTransit retry с экспоненциальным backoff; при исчерпании → status='failed' |
| LLM не забрал из очереди | RabbitMQ держит сообщение; status остаётся `sent_to_llm` |
| Ответа LLM нет за `Llm__ResultTimeoutMinutes` | reconciliation через REST `Llm__StatusBaseUrl` |
| LLM вернул `failed` | status='failed', publish AnalysisCompletedEvent(failed) → Notifications уведомляет админа |
| Дубликат LLM-ответа | `ON CONFLICT (review_id, analysis_job_id) DO NOTHING` + idempotent статус-переходы |

Все consumer'ы должны быть **идемпотентны**: одно и то же сообщение MassTransit может прийти
повторно, повторное применение не должно ломать состояние.

---

## Границы и принципы

- **Не парсим сами.** Никакого Playwright, прокси-ротации, плагинов источников. Только HTTP к Parser.
- **Не считаем агрегаты.** Этим занимается Analytics-модуль Web API. PG лишь публикует
  `AnalysisCompletedEvent`, после чего ждёт `AggregatesReadyEvent`.
- **Не генерируем PDF, не шлём Telegram, не общаемся с Frontend.** Это всё Web API.
- **Никаких прямых SQL-обращений к `webapi_db`.** PG не знает про пользователей, компании, мониторинги,
  Identity, Hangfire — только `analysisJobId` / `companyId` / `branchId` приходят в командах.
- **`obratka-jobs` — общий bucket, но раздельные ключи.** Parser → `{jobId}/raw/{source}.json`,
  PG → `{jobId}/input.json`, LLM → `{jobId}/output.json`. Никто не пишет в чужие префиксы.
- **`source` всегда slug** (`2gis | yandex | google | otzovik`) — формат Parser, его же кладём
  в БД и сообщения. Никакого `SourceType.YandexMaps` в JSON.
- **`processing_db` — единственный owner.** Web API подключается отдельным read-only
  пользователем `analytics_reader` (ADR-011 §«MVP trade-off»); `INSERT/UPDATE/DELETE` запрещены
  на уровне Postgres-прав.

---

## Открытые вопросы (унаследовано из ADR)

| Вопрос | Где решать | Источник |
|--------|------------|----------|
| Точный интервал polling Parser (3 vs 5 сек) | при первой нагрузке | ADR-001 |
| Версионирование `schema_version` LLM-сообщений | до первого деплоя с реальным LLM | ADR-004 |
| Аутентификация LLM в нашем RabbitMQ | до интеграции | ADR-004 |
| Лимит размера данных LLM (есть ли ограничение?) | при первом контакте с LLM-командой | ADR-004 |
| Алгоритм построения `composite_key` | при реализации, но детерминированный | ADR-002 |
| Disk buffering Serilog при недоступности Seq | при реализации | ADR-008 |
