# Processing Gateway — план реализации

Базируется на `CLAUDE.md`, ADR-001/002/004/008/011 и контракте Parser Service.
Существующая VPS-инфраструктура (`Parser-Service/deploy-vps.md`) уже несёт **MinIO**,
**RabbitMQ**, **Seq** и **nginx** в сети `parser-service_internal` — PG переиспользует их,
**не поднимая копий**. Доставлять нужно только сам сервис и `processing-db` (Postgres).

---

## Этап 0. Зафиксированные решения

| # | Решение |
|---|---------|
| 1 | **Runtime — .NET 9** (синхронно с Parser-ом; в Parser выбран из-за совместимости Playwright) |
| 2 | **LLM-сервиса нет** → собираем со стубом. Реальный LLM появится позже, контракт ADR-004 не меняем |
| 3 | **MassTransit `EntityFrameworkOutbox`** — атомарность `commit + publish` |
| 4 | Контракт `StartAnalysisCommand` фиксируем нашей версией; адаптируем при появлении Web API |
| 5 | Status API на VPS до Web API — **временный nginx-vhost** `gateway-dev.193.233.217.223.sslip.io` (allowlist + X-Api-Key), удаляется при появлении Web API |
| 6 | **Auto-finalize флагом** `Pipeline__AutoFinalizeWithoutAggregates=true` на dev — job через таймер из `computing_aggregates → completed`. По умолчанию `false` |
| 7 | Web API — **после PG**, Этапы 9 и 12 планируем как реальные работы |
| 8 | `composite_key` — **простая схема, без хешей**: `{source}:{branch_id}:{external_id}` если `external_id` есть; иначе `{source}:{branch_id}:{review_date.ToUnixTimeSeconds()}:{first 200 chars of trimmed text}`. UNIQUE-индекс БД ловит дубль на уровне Postgres |
| Dapper | **Используется в двух bulk-INSERT путях** (отзывы из Parser, llm-результаты из LLM); EF Core владеет схемой, миграциями, статус-машиной, single-row CRUD |

---

## Этап 1. Каркас сервиса и логирование

**Цель:** запускающийся `dotnet run` + `docker compose up`, логи в Seq, healthcheck.

1. Создать решение и проект:
   - `ProcessingGateway.sln`
   - `src/ProcessingGateway/ProcessingGateway.csproj` — `net9.0`, `Microsoft.NET.Sdk.Web`.
   - `tests/ProcessingGateway.Tests/...csproj` — xUnit, WebApplicationFactory, Testcontainers.
2. NuGet (минимум):
   - `Serilog.AspNetCore`, `Serilog.Sinks.Seq`, `Serilog.Sinks.Console`, `Serilog.Enrichers.Environment`
   - `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`
   - `Dapper`, `Npgsql` (для bulk-INSERT путей; уже подтянется как зависимость EF/Npgsql, явно фиксируем версию)
   - `MassTransit`, `MassTransit.RabbitMQ`, `MassTransit.EntityFrameworkCore` (Outbox — решение Этап 0 №3)
   - `AWSSDK.S3`
   - `Polly`, `Microsoft.Extensions.Http.Polly` (retry для Parser-клиента)
3. `Program.cs`:
   - Bootstrap Serilog по шаблону ADR-008: enricher `Service = "ProcessingGateway"`, `MachineName`,
     sinks Console + Seq (URL/ApiKey из конфига; если URL пуст — только консоль, как у Parser-а).
   - Middleware `CorrelationIdMiddleware`: читает/генерирует `X-Correlation-ID`,
     возвращает в response, пушит в `LogContext`.
   - `app.UseSerilogRequestLogging()`.
   - Эндпоинты `GET /health/live` и `GET /health/ready` (ready = БД + RabbitMQ + S3 reachable).
4. `Dockerfile` (по образцу Parser-а, но образ — обычный `mcr.microsoft.com/dotnet/aspnet:9.0`,
   Playwright не нужен).
5. `docker-compose.yml` локальный (для разработчика, см. Этап 11). Проверить, что логи приходят
   в Seq с `Service = "ProcessingGateway"`.

**Готово, когда:** контейнер поднимается, `/health/live` отвечает 200, в Seq видно событие старта.

---

## Этап 2. БД `processing_db`

**Цель:** EF Core + миграция, наличие 3 таблиц из CLAUDE.md/ADR-002.

1. `Domain/`:
   - `AnalysisJobStatus` (enum + конвертер в `varchar`).
   - `AnalysisJob`, `Review`, `ReviewLlmResult` — POCO с явным mapping в `OnModelCreating`.
   - JSONB-поля (`collection_progress`, `fake_reason_tags`, `topics`) — через Npgsql `jsonb`
     с серилизацией `System.Text.Json` (snake_case).
2. `Infrastructure/Database/ProcessingDbContext.cs` — DbContext, индексы, UNIQUE constraints,
   `gen_random_uuid()` defaults (через `HasDefaultValueSql`).
3. Первая миграция `Initial` → `dotnet ef migrations add Initial`. Применение —
   `dbContext.Database.MigrateAsync()` на старте (как делает Parser). В миграции включаем
   таблицы MassTransit Outbox (`AddInboxStateEntity`, `AddOutboxMessageEntity`,
   `AddOutboxStateEntity`) — решение №3 Этапа 0.
4. Создать роль `analytics_reader` отдельной миграцией / init-скриптом (см. Этап 11).
5. `Infrastructure/Database/IDbConnectionFactory.cs` + `NpgsqlConnectionFactory` для Dapper
   (открывает соединение по тому же `ConnectionStrings:ProcessingDb`). DI регистрирует
   и `DbContext`, и фабрику — оба бьют в одну БД, разделение чисто по use case
   (EF — статус-машина, Outbox, single-row CRUD; Dapper — bulk INSERT отзывов и LLM-результатов).
6. Тесты:
   - `ProcessingDbContextTests`: `INSERT ... ON CONFLICT (composite_key) DO NOTHING` действительно
     дедуплицирует.
   - UNIQUE `(review_id, analysis_job_id)` срабатывает.
   - GIN-индекс по `topics` создан (проверить через `pg_indexes`).
   - Testcontainers Postgres для интеграции.

**Готово, когда:** миграция применяется на чистой БД, все три таблицы и индексы существуют,
`ON CONFLICT` тесты зелёные.

---

## Этап 3. Контракт сырого отзыва + bulk INSERT (Dapper)

**Цель:** прозрачно превращать `raw/{source}.json` Parser-а в `reviews` и быстро вставлять.

Готовится для Этапа 5, но ставится отдельно ради изоляции форматных деталей.

1. `Application/Ingestion/CompositeKeyBuilder.cs` — простая детерминированная функция
   (решение №8 Этапа 0):
   ```
   if (externalId != null)  → $"{source}:{branchId:N}:ext:{externalId}"
   else                     → $"{source}:{branchId:N}:dt:{review_date.ToUnixTimeSeconds()}:{text.Trim()[..Min(200, len)]}"
   ```
   Длина строго ≤ `VARCHAR(1000)` (1000 байт хватает: slug ≤ 10 + Guid 32 + meta ≤ 30 + 200 chars текста).
   Никаких хешей — отладка проще, дубль ловит UNIQUE-индекс БД.
2. `Application/Ingestion/RawReviewMapper.cs` — `RawReview → Review`:
   - `text_language` ← `RawReview.TextLanguage` (если null — оставляем null).
   - `author_name`, `author_public_id` ← из `RawReview`.
   - `composite_key` ← через `CompositeKeyBuilder`.
   - `normalized_text` остаётся `NULL` в MVP (это зона LLM).
3. JSON-десериализация: `JsonSerializerOptions { PropertyNamingPolicy = SnakeCaseLower }` —
   ровно как пишет Parser в `S3ResultStorage.cs`.
4. **`Infrastructure/Database/RawReviewBulkInserter.cs` (Dapper):**
   ```sql
   INSERT INTO reviews
       (id, company_id, branch_id, source, external_id, composite_key,
        raw_text, text_language, review_date, stars, author_name, author_public_id, collected_at)
   VALUES (@Id, @CompanyId, @BranchId, @Source, @ExternalId, @CompositeKey,
           @RawText, @TextLanguage, @ReviewDate, @Stars, @AuthorName, @AuthorPublicId, NOW())
   ON CONFLICT (composite_key) DO NOTHING;
   ```
   Передаём массив параметров одним вызовом `connection.ExecuteAsync` — Npgsql вылетит в одну
   prepared-команду на батч (≤ 1000 строк per source). Возвращаем `inserted_count = affected_rows`.
5. Тесты на golden-фикстурах: положить `raw/yandex.json`, `raw/2gis.json`, `raw/google.json`
   из реальных результатов Parser-а в `tests/.../Fixtures/`, проверить:
   - полный roundtrip `S3 JSON → List<RawReview> → bulk INSERT → SELECT`;
   - повторный INSERT того же файла = `inserted_count == 0` (UNIQUE сработал);
   - timing: 1000 строк должны вставиться за < 200 мс на Testcontainers Postgres.

**Готово, когда:** golden-фикстуры конвертируются в строки `reviews` без потерь, дубликаты
(тот же файл дважды) дают `0 inserted`, bulk-INSERT-тест укладывается в SLO.

---

## Этап 4. Parser HTTP клиент + S3 чтение

**Цель:** PG умеет послать в Parser задачу, поллить статус, скачать результат.

1. `Infrastructure/Parser/IParserClient.cs`:
   ```csharp
   Task<Guid> StartCollectionAsync(StartCollectionRequest, CancellationToken);
   Task<CollectionTaskStatus> GetStatusAsync(Guid taskId, CancellationToken);
   Task<CollectionResultPayload> DownloadResultAsync(string s3Url, CancellationToken);
   ```
   `CollectionResultPayload` = десериализованное содержимое `raw/{source}.json`.
2. `ParserHttpClient` через `IHttpClientFactory`, `BaseAddress = Parser__BaseUrl`,
   `DelegatingHandler` для `X-Correlation-ID`.
3. Полиси Polly: `AddTransientHttpErrorPolicy` + retry + timeout per request. **Не** делаем
   глобальный fallback на failed — это решение оркестратора.
4. `Infrastructure/Storage/IJobBlobStorage.cs` + `S3JobBlobStorage`:
   - `Task<CollectionResultPayload> ReadRawAsync(Guid jobId, string sourceSlug, CancellationToken)`
   - `Task WriteInputAsync(Guid jobId, LlmInput, CancellationToken)`
   - `Task<LlmOutput> ReadOutputAsync(Guid jobId, CancellationToken)`
   - Парсинг `s3://obratka-jobs/{jobId}/raw/{source}.json` URL — отдельная функция.
5. Тесты:
   - `ParserHttpClient` против `WireMock.Net`-стуба: эмулируем `pending → running → completed`
     и проверяем, что клиент не падает на промежуточных полях `null`.
   - `S3JobBlobStorage` против Testcontainers MinIO: roundtrip read/write.

**Готово, когда:** интеграционный тест с поднятым Parser-стубом и MinIO проходит сквозную
последовательность POST → GET → download → десериализация.

---

## Этап 5. AnalysisOrchestrator + ParserPoller (FSM до `sent_to_llm`)

**Цель:** consumer на `StartAnalysisCommand` доводит job до загрузки `input.json` и публикации
LLM-сообщения.

1. `Infrastructure/Messaging/Contracts/StartAnalysisCommand.cs`:
   ```csharp
   public record StartAnalysisCommand(
       Guid AnalysisJobId, Guid CompanyId,
       List<BranchSpec> Branches,        // (BranchId, Source slug, ExternalId, ExternalUrl)
       DateTimeOffset? DateFrom, DateTimeOffset? DateTo);
   ```
   Точная форма согласовывается с Web API (см. вопрос #4).
2. `Application/Consumers/StartAnalysisCommandConsumer`:
   - `INSERT analysis_jobs (status='pending', collection_progress='{}')`.
   - `status → 'collecting'`.
   - Группирует branches по `source`, для каждой группы вызывает `IParserClient.StartCollectionAsync`,
     записывает `task_id` в `collection_progress[source]`.
   - Запускает `ParserPoller` (см. ниже).
3. `Application/Pipeline/ParserPoller`:
   - Реализация на базе `IHostedService` + per-job `CancellationTokenSource` или `Channel<JobId>`.
     Простое решение MVP — фоновый таймер, который каждые `PollIntervalSeconds` пробегает по
     active jobs (`status IN ('collecting')`) и опрашивает Parser. Идемпотентно: даже после
     рестарта PG poller возьмёт jobs из БД.
   - На каждый task: `GetStatusAsync` → обновить `collection_progress[source]`.
     На `completed` — `DownloadResultAsync` → `INSERT INTO reviews ON CONFLICT DO NOTHING`,
     обновить `review_count`.
     На `failed` — записать `error` в `collection_progress[source]`.
     На таймаут (>Parser__TaskTimeoutMinutes) — пометить источник `failed(reason=timeout)`.
4. `AnalysisOrchestrator.AdvanceAfterCollection(jobId)`:
   - все источники в `completed/failed`?
   - все failed → `status='failed'`, `AnalysisCompletedEvent(failed)`.
   - есть ≥1 completed → `status='sent_to_llm'`, `LlmDispatcher.DispatchAsync(jobId)`.
5. `LlmDispatcher`:
   - SELECT все `reviews` этого `jobId` → собрать `input.json` (структура ADR-004 §2).
   - `S3JobBlobStorage.WriteInputAsync`.
   - `analysis_jobs.payload_url`, `sent_at`.
   - Publish сообщения в `Llm__RequestQueue` (claim-check JSON).
6. Тесты:
   - Unit: `AdvanceAfterCollection` на матрице (3 источника × {completed, failed, running}).
   - Integration: stub Parser + Testcontainers Postgres+RabbitMQ+MinIO, end-to-end
     `StartAnalysisCommand → analysis_jobs.status = 'sent_to_llm'`, `input.json` лежит в S3.

**Готово, когда:** один интеграционный тест проходит pipeline `pending → collecting →
sent_to_llm`, в S3 лежит корректный `input.json` с `review_id`-ами из реальной БД.

---

## Этап 6. LLM result consumer + ингест `output.json`

**Цель:** PG ловит ответ от LLM (стуб или реальный), сохраняет результаты, публикует
`AnalysisCompletedEvent`.

1. `LlmResultMessageConsumer` слушает `Llm__ResultQueue`.
2. На `status == "failed"` → `analysis_jobs.status='failed'`, `error=...`,
   publish `AnalysisCompletedEvent(failed)`. Идемпотентность через статус-проверку.
3. На `status == "finished"`:
   - `S3JobBlobStorage.ReadOutputAsync(jobId)`.
   - `analysis_jobs.recommendation = output.recommendation`.
   - **Bulk INSERT через Dapper** в `review_llm_results`:
     ```sql
     INSERT INTO review_llm_results
         (id, review_id, analysis_job_id, fake_status, fake_reason_tags,
          sentiment, sentiment_confidence, is_spam, spam_confidence, topics, processed_at)
     VALUES (gen_random_uuid(), @ReviewId, @AnalysisJobId, @FakeStatus, @FakeReasonTags::jsonb,
             @Sentiment, @SentimentConfidence, @IsSpam, @SpamConfidence, @Topics::jsonb, NOW())
     ON CONFLICT (review_id, analysis_job_id) DO NOTHING;
     ```
     `fake_reason_tags` и `topics` сериализуем в JSON-строку и кастуем `::jsonb` —
     Dapper иначе не понимает сложные параметры в jsonb. Идемпотентность даёт UNIQUE-constraint.
   - `status → 'computing_aggregates'`, `result_url = ...`.
   - publish `AnalysisCompletedEvent(completed_pending_aggregates | partial)` через Outbox
     (commit + publish атомарно — решение №3 Этапа 0).
4. **LLM-стуб (решение №2 Этапа 0):**
   - **Отдельный проект** `tools/LlmStub/ProcessingGateway.LlmStub.csproj` (минимальный
     `.NET 9` worker), не in-process — чтобы:
     - дев-стенд на VPS не таскал стуб-код в продовом образе;
     - при появлении реального LLM — просто `docker compose stop llm-stub` без перепересборки PG.
   - Слушает `llm.requests` через MassTransit → читает `input.json` из S3 →
     синтезирует `output.json` (наивно: `sentiment` от длины и наличия слов
     «отлично/плохо», `is_spam=false`, `topics=[]`, `recommendation="(stub) ..."`)
     → пишет `output.json` в S3 → публикует в `llm.results`.
   - Включается в compose отдельным контейнером, ENV `Llm__Stub__Enabled=true` (default off
     в проде).
5. Тесты:
   - LLM-стуб как in-process consumer в Testcontainers-сценарии.
   - Идемпотентность: повторный consume того же `output.json` не плодит строк.
   - Сохранение `recommendation`.

**Готово, когда:** end-to-end `StartAnalysisCommand → AnalysisCompletedEvent` проходит локально
с LLM-стубом, в `review_llm_results` есть записи, в `analysis_jobs.recommendation` строка от стуба.

---

## Этап 7. LLM Reconciliation (REST fallback)

**Цель:** если ответа из брокера нет за `Llm__ResultTimeoutMinutes` — поллить REST `/status/{jobId}`.

1. `Application/Reconciliation/LlmStatusReconciler` — фоновая задача (`IHostedService`).
   Раз в N секунд берёт jobs со `status='sent_to_llm' AND sent_at < NOW() - timeout`.
2. HTTP GET `Llm__StatusBaseUrl/{analysisJobId}` → `{ status: processing|finished|failed,
   result_url? }`.
3. `finished` — выполнить тот же путь, что `LlmResultMessageConsumer`.
4. `failed` — `status='failed'`, error.
5. `processing` — обновить `last_reconciled_at`, ничего не делать.
6. Учитывать, что реальный LLM-сервис ещё не зафиксирован → выделить в `IExternalLlmStatusClient`
   с интерфейсом, чтобы стуб подменялся.
7. Тесты: эмулировать «брокер молчит, REST отвечает finished» через WireMock.

**Готово, когда:** интеграционный тест: брокер выключен, через таймаут reconciler находит и
завершает job по REST.

---

## Этап 8. HTTP Status API

**Цель:** `GET /api/analyses/{jobId}/status` отдаёт прогресс (формат из CLAUDE.md).

1. `Api/AnalysisStatusController.cs`:
   - `GET /api/analyses/{jobId}/status` → 200 / 404.
2. Сборка ответа: `analysis_jobs.status` → `stages[]` (3 стадии: collecting / llm_analysis /
   building_dashboard) с `state ∈ {pending, active, completed, failed}`.
   `sources` рендерится из `collection_progress` (`progress` 0–100, не 0–1 — отличие от Parser-а).
3. **Доступ:** эндпоинт **внутренний** (не пробрасываем порты в compose, не публикуем в nginx).
   Web API ходит к нему по docker-сети `http://processing-gateway:8080`.
4. На время отсутствия Web API (для разработки) — временный публичный vhost
   `gateway-dev.193.233.217.223.sslip.io` с `X-Api-Key` (по образцу `RequireQaApiKey` в Parser-е)
   и IP-allowlist. Удаляется, когда Web API появится. **Требует подтверждения от пользователя**
   (вопрос #5).
5. Тесты: матрица статусов → ожидаемый ответ.

**Готово, когда:** интеграционный тест: создаём job, прогоняем pipeline до completed,
status API отдаёт корректные `stages` и `sources` на каждом шаге.

---

## Этап 9. AggregatesReadyEvent + финализация

**Цель:** замкнуть петлю с Web API.

1. `AggregatesReadyEventConsumer`:
   - `analysis_jobs.status` `'computing_aggregates' → 'completed' | 'partial'`.
   - `partial` — если в `collection_progress` хоть один источник в `failed`.
   - `completed_at = NOW()`.
2. Если Web API ещё нет — добавить **временный** auto-finalize таймер: jobs в
   `computing_aggregates` старше N минут переводятся в `completed` без аггрегатов. Включается
   флагом `Pipeline__AutoFinalizeWithoutAggregates`. Уйдёт, когда Web API появится
   (вопрос #6).
3. `MonitoringCycleCompletedEvent` — публикуется по тому же триггеру для job-ов, помеченных
   как часть мониторинга (`StartMonitoringCycleCommand`). См. отдельный consumer
   `StartMonitoringCycleCommandConsumer` — структурно то же самое, но с другим финальным
   событием.

**Готово, когда:** sending mock `AggregatesReadyEvent` финализирует job в `completed`/`partial`.

---

## Этап 10. Сквозные интеграционные тесты

**Цель:** один тест, поднимающий полный стек контейнерами, прогоняет happy path и 2 edge-кейса.

1. `IntegrationFixture` (Testcontainers):
   - Postgres (processing-db)
   - RabbitMQ
   - MinIO (+ init bucket `obratka-jobs`)
   - WireMock-стуб Parser-а (отдельный контейнер либо in-process)
   - LLM-стуб — **тот же `tools/LlmStub`, что в проде**, поднятый как контейнер. Чтобы
     ровно тот же артефакт обкатывался в тестах и на dev-стенде.
2. Тесты:
   - **Happy path:** 3 источника, все completed, LLM finished → `analysis_jobs.status='completed'`,
     `review_count > 0`, `recommendation` непуст.
   - **Partial:** 1 источник failed, 2 ok → финал `partial`.
   - **All failed:** все источники failed → финал `failed`, в LLM ничего не уходило.
   - **LLM failed:** Parser ok, LLM вернул failed → финал `failed`.
   - **Reconciliation:** брокер ответа не выдал, reconciler через REST вытаскивает finished.
   - **Idempotency:** повторный consume `StartAnalysisCommand` с тем же `analysis_job_id`
     ничего не ломает.
3. Прогон в CI (если есть) и локально через `dotnet test --filter Category=Integration`.

**Готово, когда:** все 6 сценариев зелёные, повторный прогон тоже зелёный.

---

## Этап 11. Деплой на VPS

Используем существующую инфраструктуру (`Parser-Service/deploy-vps.md` шаги 4, 5, 7, 8, 15, 16).
Сеть `parser-service_internal`, контейнеры `minio`, `rabbitmq`, `seq`, `nginx` уже подняты.

1. **Структура каталогов** (на VPS, под `deploy@193.233.217.223`):
   ```
   ~/processing-gateway/
   ├── app/                          # код (rsync или git deploy key)
   ├── data/postgres/                # volume processing_db
   └── docker-compose.yml            # см. ниже
   ```
2. **Postgres-инстанс** (новый контейнер, **не** общий с Web API):
   ```yaml
   processing-db:
     image: postgres:16-alpine
     environment:
       POSTGRES_DB: processing
       POSTGRES_USER: processing_user
       POSTGRES_PASSWORD: ${PROCESSING_DB_PASSWORD}
     volumes:
       - ./data/postgres:/var/lib/postgresql/data
       - ./init/01-analytics-reader.sql:/docker-entrypoint-initdb.d/01-analytics-reader.sql:ro
     networks: [parser-service_internal]   # внешняя сеть Parser-а
     healthcheck:
       test: ["CMD-SHELL", "pg_isready -U processing_user -d processing"]
   ```
   `01-analytics-reader.sql` создаёт роль `analytics_reader` с `SELECT` на 3 таблицы (после
   первой миграции PG, либо `CREATE ROLE ... IF NOT EXISTS`-обёртка). Пароль — через ENV
   `ANALYTICS_READER_PASSWORD`.
3. **Сервис PG**:
   ```yaml
   processing-gateway:
     build: { context: ./app, dockerfile: src/ProcessingGateway/Dockerfile }
     environment:
       ASPNETCORE_ENVIRONMENT: Production
       ConnectionStrings__ProcessingDb: "Host=processing-db;Database=processing;Username=processing_user;Password=${PROCESSING_DB_PASSWORD}"
       Parser__BaseUrl: "http://parser:8080"               # внутренний DNS docker
       Parser__PollIntervalSeconds: "4"
       S3__Endpoint: "http://minio:9000"
       S3__AccessKey: "${MINIO_ACCESS_KEY}"
       S3__SecretKey: "${MINIO_SECRET_KEY}"
       S3__BucketName: "obratka-jobs"
       RabbitMq__Host: "rabbitmq"
       RabbitMq__User: "${RABBIT_USER}"
       RabbitMq__Pass: "${RABBIT_PASS}"
       Llm__RequestQueue: "llm.requests"
       Llm__ResultQueue: "llm.results"
       Llm__ResultTimeoutMinutes: "30"
       Pipeline__AutoFinalizeWithoutAggregates: "true"      # решение №6 Этапа 0; снять при появлении Web API
       Pipeline__AutoFinalizeAfterMinutes: "5"
       Gateway__ApiKey: "${GATEWAY_API_KEY}"                 # для status-API на dev-vhost (решение №5)
       Seq__ServerUrl: "http://seq:80"
       Seq__ApiKey: "${SEQ_INGESTION_KEY_GATEWAY}"           # отдельный ингест-ключ Seq
     depends_on:
       processing-db: { condition: service_healthy }
     networks: [parser-service_internal]
     # порты НЕ пробрасываем — статус-API внутренний, наружу — только через nginx vhost
   ```
4. **LLM-стуб** (решение №2 Этапа 0) — отдельный контейнер из той же сборки:
   ```yaml
   llm-stub:
     build: { context: ./app, dockerfile: tools/LlmStub/Dockerfile }
     environment:
       RabbitMq__Host: "rabbitmq"
       RabbitMq__User: "${RABBIT_USER}"
       RabbitMq__Pass: "${RABBIT_PASS}"
       S3__Endpoint: "http://minio:9000"
       S3__AccessKey: "${MINIO_ACCESS_KEY}"
       S3__SecretKey: "${MINIO_SECRET_KEY}"
       S3__BucketName: "obratka-jobs"
       Llm__RequestQueue: "llm.requests"
       Llm__ResultQueue: "llm.results"
       Seq__ServerUrl: "http://seq:80"
       Seq__ApiKey: "${SEQ_INGESTION_KEY_LLMSTUB}"
     networks: [parser-service_internal]
     # выключается одним `docker compose stop llm-stub` при подключении реального LLM
   ```
5. **Внешняя сеть compose**:
   ```yaml
   networks:
     parser-service_internal:
       external: true
   ```
6. **Seq ingestion key**: создать в UI Seq два отдельных ключа — `processing-gateway` и
   `llm-stub` (permission `Ingest`), положить в `~/processing-gateway/.env`
   (см. `deploy-vps.md` §16.5).
7. **Dev nginx-vhost** (решение №5 Этапа 0) — отдельный server-block в каталоге Parser-а
   (там живёт nginx). Файл `~/parser-service/nginx/conf.d/gateway.conf`:
   - DNS: добавить A-запись `gateway-dev.193.233.217.223.sslip.io` → IP VPS.
   - Расширить сертификат через `certbot --expand -d ... -d gateway-dev.193.233.217.223.sslip.io`
     (как в `deploy-vps.md` §15.1 / §16.3).
   - Конфиг — копия `parser.conf` с `proxy_pass http://processing-gateway:8080;`,
     IP-allowlist + проверка `X-Api-Key` через middleware PG (см. `RequireQaApiKey` Parser-а
     как образец).
   - Удалить файл и обнулить DNS, когда Web API появится.
8. Чеклист после первого `docker compose up -d --build`:
   - `docker compose logs processing-gateway` — миграция применилась, нет паник.
   - В Seq появились `Service = ProcessingGateway, Started` и `Service = LlmStub, Started`.
   - `psql` под `analytics_reader` может `SELECT * FROM reviews LIMIT 1`, но не `INSERT`.
   - Через RabbitMQ Management (SSH-туннель) видны очереди `llm.requests`, `llm.results`,
     `processing-gateway-*` consumer-очереди MassTransit, `processing-gateway-outbox`-таблицы
     в БД.
   - `curl -H "X-Api-Key: $GATEWAY_API_KEY" https://gateway-dev.193.233.217.223.sslip.io/health/ready`
     → `200`.
9. **Бэкапы Postgres** — `pg_dump` по cron (по аналогии с шагом 14 deploy-vps.md):
   ```
   0 3 * * * docker exec processing-gateway-processing-db-1 pg_dump -U processing_user processing | gzip > /home/deploy/backups/processing-$(date +\%F).sql.gz
   ```

---

## Этап 12. После Web API (вне scope этого плана, но фиксируем выходы)

Когда появится Web API:
- `StartAnalysisCommand` начнёт публиковаться оттуда (Web API → broker → PG). Контракт пока
  фиксируется нашей версией (решение №4 Этапа 0); под Web API адаптируется.
- `AggregatesReadyEvent` начнёт приходить от Web API → выключить
  `Pipeline__AutoFinalizeWithoutAggregates=false` (решение №6 Этапа 0).
- Status API становится строго внутренним → удалить vhost `gateway-dev` и
  `Gateway__ApiKey`-проверку.
- Подключить Web API к `processing_db` через `analytics_reader`-creds.
- Когда подключится реальный LLM — `docker compose stop llm-stub`, ничего больше не меняем
  (контракт ADR-004 один и тот же для стуба и реального сервиса).

---

## QA-эндпоинты (для отладки PG → Parser → LLM)

Подход — копия Parser-а: атрибут `RequireQaApiKey` (X-Api-Key из ENV `Gateway__ApiKey`),
маршруты с префиксом `/api/qa/...`. Доступны только при наличии валидного ключа; на dev-vhost
дополнительно режутся IP-allowlist-ом nginx-а. Реализуются по ходу этапов 4–8.

### Bootstrap (заменяет Web API на время разработки)

| Метод | Путь | Назначение | На каком этапе |
|-------|------|-----------|---------------|
| `POST` | `/api/qa/analyses` | Создать `analysis_job` и опубликовать `StartAnalysisCommand` в брокер. Body — наша версия команды (companyId, branches, dateFrom?, dateTo?). Возвращает `analysisJobId`. **Ровно как это сделает Web API** — отлаживаем через брокер, не дёргаем consumer напрямую | 5 |
| `POST` | `/api/qa/monitoring-cycles` | Аналогично, но `StartMonitoringCycleCommand` | 9 |
| `POST` | `/api/qa/analyses/{jobId}/finalize` | Публикует мокнутый `AggregatesReadyEvent` для `jobId` (имитация Analytics-модуля Web API, не дожидаясь auto-finalize таймера) | 9 |
| `POST` | `/api/qa/analyses/{jobId}/cancel` | Принудительно `status='failed'`, `error='manual cancel'` (для отладки залипших job-ов) | 5 |

### Парсер-сторона (PG → Parser)

| Метод | Путь | Назначение | На каком этапе |
|-------|------|-----------|---------------|
| `GET` | `/api/qa/analyses/{jobId}` | Полный debug-снимок строки `analysis_jobs` + актуальный `collection_progress` (расшифрованный JSONB). Без 404-trim-ов — чтобы видеть ровно то, что в БД | 5 |
| `GET` | `/api/qa/analyses/{jobId}/reviews?source=&limit=` | Список собранных `reviews` из БД с фильтрами per-source — проверить, что ингест отработал | 5 |
| `POST` | `/api/qa/parser/restart-source/{jobId}/{source}` | Создать **новую** `task_id` в Parser для одного источника job-а (если оригинальный таск завис/упал). Старая `task_id` в `collection_progress` затирается | 5 |
| `GET` | `/api/qa/parser/ping` | Проксирует `GET {Parser__BaseUrl}/health/...` — проверить, что PG видит Parser в docker-сети | 4 |

### LLM-сторона (PG ↔ LLM stub)

| Метод | Путь | Назначение | На каком этапе |
|-------|------|-----------|---------------|
| `POST` | `/api/qa/llm/replay/{jobId}` | Перечитать `reviews` job-а, перезаписать `input.json` и заново опубликовать LLM-request. Идемпотентно (LLM-result consumer всё равно отбросит дубль через UNIQUE-constraint) | 6 |
| `POST` | `/api/qa/llm/inject/{jobId}` | Принять `output.json` в теле запроса, **записать в S3 от своего имени**, опубликовать `{status:finished, result_url:...}` в `llm.results` — имитируем реальный LLM с произвольным payload без зависимости от стуба. Главный инструмент для отладки ингеста | 6 |
| `POST` | `/api/qa/llm/fail/{jobId}?error=...` | Опубликовать `{status:failed, error}` в `llm.results` — тест failure path | 6 |
| `POST` | `/api/qa/llm/timeout/{jobId}` | Сдвинуть `analysis_jobs.sent_at` в прошлое (`NOW() - timeout - 1m`), чтобы reconciler увидел job — тест REST fallback | 7 |

### Диагностика инфраструктуры

| Метод | Путь | Назначение | На каком этапе |
|-------|------|-----------|---------------|
| `GET` | `/health/live` | стандартный liveness (нет авторизации) | 1 |
| `GET` | `/health/ready` | readiness — Postgres + RabbitMQ + MinIO + Parser достижимы (нет авторизации, защищается на уровне сети) | 1 |
| `GET` | `/api/qa/health/dependencies` | Расширенная диагностика — версия Postgres, статус Outbox-очереди, размер бакета `obratka-jobs`, RabbitMQ queue-depth для `llm.requests`/`llm.results` | 5 |
| `GET` | `/api/qa/outbox?status=&limit=` | Состояние MassTransit Outbox: сколько unsent, последний sent_at, какие сообщения залипли | 6 |
| `GET` | `/api/qa/jobs/{jobId}/blobs` | Листинг S3-ключей префикса `{jobId}/` с size + last_modified — на одном экране видно `raw/*.json`, `input.json`, `output.json` | 4 |
| `GET` | `/api/qa/jobs/{jobId}/blobs/{name}` | Отдать тело конкретного блоба (raw/yandex.json, input.json, output.json). Поток без буферизации, без декомпрессии | 4 |

### Что **не** делать QA-ручкой

- Не строить «зеркало» Parser-а (search, /collection-tasks/*) — он уже выставлен на VPS, ходим прямо туда.
- Не делать ручку «накатить миграцию заново» — миграции применяются только на старте PG, чтобы не было параллельных DDL под live-трафиком.
- Не выставлять `psql`-прокси / SQL-runner — у админа есть `analytics_reader` на чтение и SSH к контейнеру для `\dt`.

При прибытии Web API весь блок «Bootstrap» выпиливается; «Парсер-сторона», «LLM-сторона», «Диагностика»
остаются — они полезны и при наличии Web API.

---

## Технические замечания, не привязанные к этапу

- **Идемпотентность всех consumer-ов** обязательна — MassTransit (даже с Outbox) может
  доставить дубль на consume-стороне. Проверка по `analysis_jobs.status` перед каждым переходом.
- **Транзакционность ингеста** результатов LLM: `INSERT review_llm_results` + `UPDATE
  analysis_jobs` + публикация `AnalysisCompletedEvent` — всё в одной EF-транзакции через
  `EntityFrameworkOutbox` (решение №3 Этапа 0). Outbox-publisher достаёт сообщения и шлёт в
  RabbitMQ уже после commit.
- **EF Core vs Dapper — разделение зон.** EF владеет `analysis_jobs`, Outbox-таблицами,
  схемой. Dapper — **только** на двух bulk-INSERT путях (`reviews` после Parser-а и
  `review_llm_results` после LLM). Никаких аналитических SELECT-ов в PG нет — это зона
  Web API Analytics-модуля (читает по `analytics_reader`).
- **`source` всегда slug** (`2gis | yandex | google | otzovik`) в БД и сообщениях. Никаких
  `SourceType.YandexMaps` за пределами C#-кода.
- **`composite_key`-схема** фиксируется в коде и тестах **до** первой выкатки на VPS — менять
  её потом дорого (придётся пересчитывать существующие строки).
- **`obratka-jobs` shared bucket**: PG пишет только в `{jobId}/input.json`, читает только
  `{jobId}/raw/*.json` и `{jobId}/output.json`. LLM-стуб пишет `{jobId}/output.json`.
  Никто не пишет в чужие префиксы.

---

## Открытые вопросы

Все основные решения зафиксированы в Этапе 0. Остаются мелкие технические вопросы,
решаются при реализации:

| # | Вопрос | Где решать |
|---|--------|------------|
| 1 | Точный интервал polling Parser (3 / 4 / 5 сек) | Этап 5, после первой нагрузочной прогонки |
| 2 | Disk-buffering Serilog при недоступности Seq (`bufferBaseFilename`) | Этап 1, по факту первого падения |
| 3 | Версионирование `schema_version` LLM-сообщений — сейчас `"1.0"`, политика breaking changes | До первого реального LLM-деплоя |
| 4 | Аутентификация реального LLM в RabbitMQ (отдельный vhost / user / TLS) | До интеграции с реальным LLM |
| 5 | Лимит размера `input.json` со стороны LLM | При первом контакте с LLM-командой |
