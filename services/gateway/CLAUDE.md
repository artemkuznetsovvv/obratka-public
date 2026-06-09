# Processing Gateway

Оркестратор pipeline анализа отзывов: получает команды от Web API → запускает сбор в Parser
Service → сохраняет сырые отзывы в `processing_db` → публикует claim-check во внешний LLM →
сохраняет результаты → уведомляет Web API. **Единственная точка интеграции с внешним LLM**.

> Источники истины:
> - `../ADR-obratka-app/.../overview.md` — общий контекст системы.
> - ADR-001 (Parser ↔ PG), ADR-002 (схема `processing_db`), ADR-004 (LLM-транспорт), ADR-008 (логи), ADR-011 (декомпозиция, отдельные БД).
> - `../Parser-Service/CLAUDE.md` + `../Parser-Service/docs/flow-parser.md` — контракт Parser, который PG **обязан** соблюдать.

> **Запросы на улучшения из соседних сервисов** (Web API, UI) —
> в [`processing-gateway-todo.md`](processing-gateway-todo.md) в корне репы.
> Прежде чем планировать работу — проверь, нет ли уже описанного запроса там.

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
│   └── Reconciliation/                  ← (future, Этап 7 отложен)
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
│   │   ├── IJobBlobStorage.cs            ← read raw/{source}.json, write input.json, read output_reviews.json + output_summary.json
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
| `Seq__ServerUrl` | `http://seq:5341` | централизованные логи (ADR-008); опц. `Seq__ApiKey` |

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

Создаётся миграцией EF Core. Отклонения от ADR-002:
- **`reviews.id` — `bigint`** (а не UUID), потому что таблица high-volume и нам важен компактный
  PK + sequential insert pattern. См. IMPLEMENTATION_PLAN.md решение №3.
- **`review_llm_results` под schema 2.0** — нет `fake_status`/`is_spam`, есть `aspects` JSONB.
- **`analysis_jobs` под schema 2.0** — вместо `recommendation TEXT` — `summary TEXT` +
  `recommendations_count INT`, плюс отдельная таблица `analysis_recommendations` (1:N).

```sql
CREATE TABLE reviews (
    id              BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,   -- bigint, не UUID
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

-- Per-review результат LLM (schema 2.0).
CREATE TABLE review_llm_results (
    id                   BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    review_id            BIGINT NOT NULL REFERENCES reviews(id),
    analysis_job_id      UUID NOT NULL,
    overall_sentiment    VARCHAR(20) NOT NULL,                 -- 'позитивный'|'негативный'|'нейтральный'
    overall_confidence   FLOAT NOT NULL,                       -- 0..1
    aspects              JSONB NOT NULL DEFAULT '[]',          -- массив { topic, sentiment, confidence, fragment, is_freeform }
    processed_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (review_id, analysis_job_id)
);
CREATE INDEX idx_llm_results_job     ON review_llm_results (analysis_job_id);
CREATE INDEX idx_llm_results_aspects ON review_llm_results USING GIN (aspects);

-- Job-level summary (schema 2.0).
CREATE TABLE analysis_jobs (
    id                    UUID PRIMARY KEY,
    company_id            UUID NOT NULL,
    status                VARCHAR(40) NOT NULL,
    review_count          INT NOT NULL DEFAULT 0,
    collection_progress   JSONB NOT NULL DEFAULT '{}',
    payload_url           TEXT,                           -- s3://.../input.json
    result_reviews_url    TEXT,                           -- s3://.../output_reviews.json
    result_summary_url    TEXT,                           -- s3://.../output_summary.json
    summary               TEXT,                           -- LLM `summary` (1-3 предложения)
    recommendations_count INT NOT NULL DEFAULT 0,         -- денормализация для UI/быстрых запросов
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at               TIMESTAMPTZ,
    completed_at          TIMESTAMPTZ,
    error                 TEXT
);

-- Job-level рекомендации, 1:N к analysis_jobs (schema 2.0).
CREATE TABLE analysis_recommendations (
    id                BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    analysis_job_id   UUID NOT NULL REFERENCES analysis_jobs(id) ON DELETE CASCADE,
    priority          SMALLINT NOT NULL CHECK (priority BETWEEN 1 AND 3),
    topic             VARCHAR(200) NOT NULL,
    title             TEXT NOT NULL,
    body              TEXT NOT NULL,
    expected_impact   TEXT,
    evidence          JSONB NOT NULL DEFAULT '[]',         -- массив строк (цитаты или review_id)
    sort_order        INT NOT NULL,                        -- порядок из output_summary (для стабильного рендера)
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_recommendations_job_priority
    ON analysis_recommendations (analysis_job_id, priority, sort_order);

-- m2m связь job ↔ review (какие отзывы попали в данный анализ).
CREATE TABLE analysis_job_reviews (
    analysis_job_id  UUID   NOT NULL REFERENCES analysis_jobs(id) ON DELETE CASCADE,
    review_id        BIGINT NOT NULL REFERENCES reviews(id),
    PRIMARY KEY (analysis_job_id, review_id)
);
CREATE INDEX idx_ajr_review ON analysis_job_reviews (review_id);
```

### Дедупликация при вставке

```csharp
// Ингест raw/{source}.json от Parser:
INSERT INTO reviews (...) VALUES (...)
ON CONFLICT (composite_key) DO NOTHING;

// Ингест output_reviews.json от LLM:
INSERT INTO review_llm_results (...) VALUES (...)
ON CONFLICT (review_id, analysis_job_id) DO NOTHING;
```

`composite_key` формирует PG (детерминированно), а не Parser — у Parser нет понятия `normalized_text`.

`analysis_recommendations` пишется **`DELETE WHERE analysis_job_id = X` + bulk INSERT** при
повторном получении output_summary (replay) — это проще, чем пытаться upsert по содержимому,
и идемпотентно.

### Владение

| Таблица | Пишет | Читает |
|---------|-------|--------|
| `reviews`, `review_llm_results`, `analysis_jobs`, `analysis_recommendations`, `analysis_job_reviews` | **только** Processing Gateway | Web API Analytics-модуль (`analytics_reader`, read-only, ADR-011) |

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
   → computing_aggregates   (LLM вернул output_reviews.json + output_summary.json, оба распарсены и сохранены,
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

## Контракт с внешним LLM (ADR-004, schema_version `2.0`)

LLM-сервис — `obratka/llm_pipeline` (Python, OpenRouter за фасадом). Полный контракт —
`LLM_PYTHON_QUICKSTART.md` (наш) + `llm_pipline/llm_contracts_changed.md` (зеркало от LLM-команды).
Этот раздел — снапшот для PG-разработчика.

Транспорт — RabbitMQ + claim-check через MinIO. **На выходе — два файла**: `output_reviews.json`
(per-review aspect-based анализ) и `output_summary.json` (job-level рекомендации).

### `s3://obratka-jobs/{jobId}/input.json` (PG пишет, LLM читает)

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "<uuid>",
  "company_id": "<uuid>",
  "reviews": [
    {
      "review_id": <bigint>,
      "text": "...",
      "source": "2gis",
      "date": "2026-04-15T16:50:56.684268+00:00",
      "stars": 5,
      "branch_id": "<uuid>",
      "text_language": "ru"
    }
  ]
}
```

`review_id` — `reviews.id` (bigint в нашей БД). LLM **обязан** сохранять и тип, и значение
в output, иначе матчинг сломается.

### Сообщение `LlmRequestMessage` (PG публикует в `Llm__RequestQueue` через MassTransit envelope)

```json
{
  "analysis_job_id": "<uuid>",
  "company_id": "<uuid>",
  "payload_url": "s3://obratka-jobs/<jobId>/input.json",
  "review_count": 847,
  "schema_version": "2.0",
  "callback_queue": "llm.results"
}
```

При сборке message PG **обязан** установить `CorrelationId = AnalysisJobId` (через
`CorrelatedBy<Guid>` или explicit `SendContext.CorrelationId`), иначе сквозная трассировка
через Seq потеряется.

### Сообщение `LlmResultMessage` (PG подписан на `Llm__ResultQueue`)

LLM публикует **raw JSON** (без MassTransit envelope). Соответственно PG-consumer для
этой очереди настроен на `e.UseRawJsonDeserializer()`.

```json
{
  "analysis_job_id":      "<uuid>",
  "status":               "finished",
  "result_reviews_url":   "s3://obratka-jobs/<jobId>/output_reviews.json",
  "result_summary_url":   "s3://obratka-jobs/<jobId>/output_summary.json",
  "schema_version":       "2.0"
}
```

При `status == "failed"` оба URL отсутствуют, есть `error: "..."`.

### `s3://obratka-jobs/{jobId}/output_reviews.json` (LLM пишет, PG читает)

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "<uuid>",
  "reviews": [
    {
      "review_id": 601,
      "text": "потрясающе атмосферное место...",
      "overall_sentiment": "позитивный",
      "overall_confidence": 0.9,
      "aspects": [
        { "topic": "атмосфера", "sentiment": "позитивный", "confidence": 0.9, "fragment": "...", "is_freeform": false },
        { "topic": "еда/напитки", "sentiment": "позитивный", "confidence": 0.85, "fragment": "...", "is_freeform": false }
      ]
    }
  ]
}
```

Заметки:
- `overall_sentiment` — русский enum: `позитивный` | `негативный` | `нейтральный`. Других значений нет.
- `aspects[]` — массив объектов с полями `topic`/`sentiment`/`confidence`/`fragment`/`is_freeform`.
- Один и тот же `topic` может встречаться **несколько раз** в `aspects` одного review с разными
  `sentiment` (например: «персонал в целом негативно, новый администратор позитивно»). PG-сторона
  это допускает.
- `aspects: []` — допустимо (модель не нашла уверенных тем); запись review всё равно сохраняется.
- `aspects[].fragment` может быть пустой строкой (тема извлечена без однозначной цитаты).

Маппинг в БД: одна запись в `review_llm_results` per `review_id` × `analysis_job_id` (UNIQUE),
`aspects` хранится как `JSONB`, `text` не дублируется (он в `reviews.raw_text`).

### `s3://obratka-jobs/{jobId}/output_summary.json` (LLM пишет, PG читает)

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "<uuid>",
  "recommendations_count": 8,
  "summary": "На основе анализа отзывов и KPI...",
  "full_recommendations": [
    {
      "priority": 1,
      "topic": "персонал",
      "title": "Улучшение коммуникации и обучения персонала",
      "body": "...",
      "expected_impact": "...",
      "evidence": ["цитата 1", "цитата 2"]
    }
  ]
}
```

Заметки:
- `recommendations_count = len(full_recommendations)` (PG проверяет как инвариант).
- `full_recommendations[]` отсортирован по `priority` ASC.
- `priority`: `1` критично, `2` важно, `3` полезно.
- `evidence: []` допустимо, отсутствие поля — нет.

Маппинг в БД:
- `summary` → `analysis_jobs.summary`
- `recommendations_count` → `analysis_jobs.recommendations_count`
- каждый элемент `full_recommendations[]` → строка в `analysis_recommendations` (см. схему БД).

### Изменения относительно `1.0` (для миграции PG-кода)

| Что было в `1.0` | Что стало в `2.0` |
|---|---|
| Один `output.json` | **Два файла**: `output_reviews.json` + `output_summary.json` |
| `LlmResultMessage.result_url` | `result_reviews_url` + `result_summary_url` |
| `output.json.processedReview[]` (camelCase) | `output_reviews.json.reviews[]` (snake_case) |
| `processedReview[].fake_status` / `fake_reason_tags` / `is_spam` / `spam_confidence` | **удалены** (LLM-команда не извлекает эти сигналы) |
| `processedReview[].sentiment` (en: `positive`/`negative`/...) | `overall_sentiment` (ru: `позитивный`/`негативный`/`нейтральный`) |
| `processedReview[].topics: string[]` | `aspects[]` с полями `topic`/`sentiment`/`confidence`/`fragment`/`is_freeform` |
| `output.json.recommendation: string` | удалено; **новые** поля `summary` + `full_recommendations[]` |
| `LlmResultMessage` через MassTransit envelope | **raw JSON** (PG-consumer на `UseRawJsonDeserializer()`) |

### Reconciliation (ADR-004 §4) — **отложено**

На MVP не реализовано (см. `IMPLEMENTATION_PLAN.md` Этап 7). Ручное восстановление —
через QA-ручку `POST /api/qa/llm/replay/{jobId}`. ENV `Llm__StatusBaseUrl` и
`Llm__ResultTimeoutMinutes` зарезервированы. Когда подключим — будем поллить
`http://llm-pipeline:8000/status/{jobId}` (REST endpoint LLM-сервиса, см. QUICKSTART §5).

---

## Шина: MassTransit / RabbitMQ

### Подписки (consumers)

| Сообщение | От кого | Что делает |
|-----------|---------|-----------|
| `StartAnalysisCommand { analysisJobId, companyId, branches[], dateFrom?, dateTo? }` | Web API | создаёт `analysis_jobs(pending)`, переводит в `collecting`, запускает Parser-таски + polling |
| `StartMonitoringCycleCommand { monitoringId, companyId, branches[], lastCollectedAt }` | Web API (Hangfire) | то же, но `dateFrom = lastCollectedAt`; в конце публикует `MonitoringCycleCompletedEvent` |
| `AggregatesReadyEvent { analysisJobId, companyId }` | Web API (Analytics) | финализирует `analysis_jobs.status = completed/partial`, проставляет `completed_at` |
| `LlmResultMessage` (raw JSON) | LLM | ingest output_reviews.json + output_summary.json |

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

LlmResultMessageConsumer (Llm__ResultQueue, raw JSON):
  ├── при status='failed' → status='failed', publish AnalysisCompletedEvent(failed)
  └── при status='finished':
        ├── скачать output_reviews.json + output_summary.json (parallel)
        ├── валидация: оба analysis_job_id == jobId; recommendations_count == len(full_recommendations)
        ├── BULK INSERT review_llm_results (overall_sentiment, overall_confidence, aspects)
        │     ON CONFLICT (review_id, analysis_job_id) DO NOTHING
        ├── analysis_jobs.summary = output_summary.summary
        ├── analysis_jobs.recommendations_count = output_summary.recommendations_count
        ├── DELETE FROM analysis_recommendations WHERE analysis_job_id = jobId  (для replay-идемпотентности)
        ├── BULK INSERT analysis_recommendations (priority, topic, title, body, expected_impact, evidence, sort_order)
        ├── analysis_jobs.result_reviews_url = ..., result_summary_url = ...
        ├── status → 'computing_aggregates'
        └── publish AnalysisCompletedEvent(completed_pending_aggregates | partial)

AggregatesReadyEventConsumer (от Web API):
  └── status → 'completed' (или 'partial' если на этапе сбора были failed),
      analysis_jobs.completed_at = NOW()
```

Live-мониторинг идёт по тому же пути, но с `dateFrom = lastCollectedAt` и финальной публикацией
`MonitoringCycleCompletedEvent`.

---

## Логирование и Correlation ID (ADR-008)

Единая модель трейсинга (см. корневой `../logging-trace-plan.md`): **первичный сквозной трейс
на анализ = `AnalysisJobId`** (фильтр в Seq по нему даёт весь анализ через Web API + PG + Parser);
`CorrelationId` (`X-Correlation-ID`) — id одной HTTP-цепочки. Имена свойств `LogContext` едины
между сервисами: `CorrelationId`, `AnalysisJobId`, `CompanyId`, `Initiator` (в PG всегда
`system:*` — про пользователей PG не знает), `Source`/`TaskId` (в Parser).

- Serilog → Seq, sink `WriteTo.Seq(Seq:ServerUrl)` (+ опц. `Seq:ApiKey`).
- Enricher: `Service = "ProcessingGateway"`, `MachineName`.
- HTTP middleware читает `X-Correlation-ID`, иначе генерирует Guid.ToString("N");
  кладёт в `LogContext` и отдаёт обратно в response header.
- Каждый исходящий вызов `ParserHttpClient` добавляет `X-Correlation-ID` (DelegatingHandler).
- MassTransit `CorrelationId` envelope ↔ Serilog `LogContext.PushProperty("CorrelationId", ...)`
  в каждом consumer'е. На исходящих `Send`/`Publish` (LlmRequestMessage, AnalysisCompletedEvent,
  StartAnalysisCommand) envelope `CorrelationId = AnalysisJobId` выставляется явно — гэп закрыт
  (`LlmDispatcher`, `AnalysisOrchestrator`, `LlmResultIngestor`, `QaAnalysesController`).
- В каждом consumer'е/pipeline-классе пушим `AnalysisJobId`, `CompanyId` — для трейсинга через Seq.
- `UseSerilogRequestLogging.EnrichDiagnosticContext` кладёт на строку request-summary `Direction`,
  и `AnalysisJobId` (из route `{jobId}` status-эндпоинта или `HttpContext.Items`).
- `QaAnalysesController` (`POST /api/qa/analyses`) — **pivot**: здесь рождается `AnalysisJobId`,
  а `CorrelationId` запроса уже в scope → одна строка связывает цепочку запроса Web API с трейсом анализа.
- `Microsoft.AspNetCore` и `System.Net.Http` — на `Warning` (как в ADR-008 §template).
- Внешний LLM (Python) в Seq не пишет: в таймлайне по `AnalysisJobId` ожидаемый разрыв на время
  работы LLM, затем `LlmResultMessageConsumer` восстанавливает `CorrelationId=AnalysisJobId` — норма.

Ключевые события (ADR-008 §«Что и как логируется»):
- Information: задача Parser создана, отзывы сохранены (`count`), LLM pipeline запущен,
  output_reviews.json + output_summary.json применены, status-переход.
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
| Ответа LLM нет за `Llm__ResultTimeoutMinutes` | **MVP**: ручное восстановление через QA `POST /api/qa/llm/replay/{jobId}` (Этап 8). **Future**: автоматический reconciliation REST `Llm__StatusBaseUrl` — отложено, см. IMPLEMENTATION_PLAN.md Этап 7 |
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
  PG → `{jobId}/input.json`, LLM → `{jobId}/output_reviews.json` + `{jobId}/output_summary.json`. Никто не пишет в чужие префиксы.
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
