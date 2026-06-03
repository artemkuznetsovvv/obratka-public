# Processing-Gateway · TODO

Реестр улучшений PG, инициированных из соседних сервисов (Web API, UI).
Каждый пункт самодостаточный: кто хочет, зачем и что конкретно поменять.

---

## 1. Хранить period_from / period_to на analysis_jobs

**Откуда запрос:** Web API → HistoryDetailPage (`/history/:jobId`).
**Приоритет:** средний — UX-баг для re-visit старых анализов; критично перед live-monitoring.
**Дата:** 2026-05-26.

### Контекст

PG принимает `dateFrom` / `dateTo` в `StartAnalysisCommand` (см. ADR-004,
`Application/Consumers/StartAnalysisCommandConsumer.cs`) и пробрасывает их в
`CreateParserCollectionTaskRequest` для каждого источника. **Но сам PG их не
сохраняет** на `analysis_jobs` — после старта период живёт только в JSON
запросов к Parser-Service (`BranchesJson` в SQLite парсера).

В результате `AnalysisJobDto`, который PG отдаёт в `GET /api/qa/analyses/{id}`,
не содержит периода. Web API на детальной странице (`HistoryDetailPage`)
вынужден показывать период из `Company.draftPeriodFrom/To`, что **некорректно**:

- Юзер запускает анализ за `01.01—31.05` → `Company.draft*` = эта дата, детальная корректна
- Юзер меняет настройки мастера для нового анализа на `01.06—31.07` → `Company.draft*` = новая дата
- Юзер открывает **старый** анализ → детальная показывает `01.06—31.07` ❌

Период — это **параметр запуска**, должен жить рядом с самим job-ом, не зависеть
от состояния Company. Для live-monitoring это станет критично: каждый цикл
мониторинга — свой период (`dateFrom = lastCollectedAt, dateTo = now`),
исторически потребуется видеть какой период каждый цикл покрыл.

### Что нужно поменять

**В Processing-Gateway:**

1. EF миграция: добавить колонки в `analysis_jobs`:
   ```sql
   ALTER TABLE analysis_jobs
     ADD COLUMN period_from TIMESTAMPTZ NULL,
     ADD COLUMN period_to   TIMESTAMPTZ NULL;
   ```

2. `Domain/AnalysisJob.cs`: добавить свойства
   ```csharp
   public DateTimeOffset? PeriodFrom { get; set; }
   public DateTimeOffset? PeriodTo { get; set; }
   ```

3. `Application/Consumers/StartAnalysisCommandConsumer.cs`: при `INSERT analysis_jobs`
   сохранять `command.DateFrom` / `command.DateTo`.

4. `Application/Consumers/StartMonitoringCycleCommandConsumer.cs`: то же —
   сохранять period_from (= `lastCollectedAt`) и period_to (= `now`).

5. Расширить QA-эндпоинт `GET /api/qa/analyses/{id}` чтобы возвращать новые
   поля (JSON snake_case: `period_from`, `period_to`).

6. Расширить `GET /api/qa/analyses` (list) аналогично.

**В Web API:**

7. `Integration/ProcessingGateway/Contracts/AnalysisJobContracts.cs`:
   - `RawAnalysisJob` — добавить `PeriodFrom` / `PeriodTo` с `JsonPropertyName("period_from"/"period_to")`
   - `AnalysisJobDto` — добавить публичные поля `DateTimeOffset? PeriodFrom, PeriodTo`
   - `AnalysisJobMapping.ToDto` — пробросить

**На фронте:**

8. `api/admin.ts` → `AnalysisJob` — добавить `periodFrom: string | null, periodTo: string | null`.

9. `pages/history/HistoryDetailPage.tsx`:
   - убрать чтение из `companyQuery.data.draftPeriodFrom/To`
   - читать `jobQuery.data.periodFrom/To`
   - `null/null` → «С самого начала»

10. Также `HistoryListPage.tsx` (карточки в списке) при необходимости показывать
    период каждого анализа индивидуально.

### Acceptance criteria

- Запустил анализ за период X → детальная показывает X
- Изменил настройки мастера на Y, запустил новый анализ → открыл **старый** анализ →
  показывает X (не Y)
- Live-monitoring: каждый цикл сохраняет свой `(period_from, period_to)`,
  они видны на детальной странице
- Старые анализы без сохранённого периода (до миграции) показывают «—» или
  «С самого начала» (на выбор — null допустим)

---

## 2. Snapshot выбранных филиалов на старте job-а

**Откуда запрос:** Web API → HistoryDetailPage (collapsible «Параметры анализа»).
**Приоритет:** средний — связан с пунктом 1 (та же проблема перегруппировки).
**Дата:** 2026-05-26.

### Контекст

На детальной странице анализа есть раскрывающийся блок «Параметры анализа» —
показывает выбранные физические филиалы и провайдеры. Сейчас Web API читает
**текущее** состояние группировки из `webapi_db.LogicalBranches` (через
`companiesApi.listGroups`). Если юзер успел перегруппировать после запуска
анализа — на детальной увидит новую группировку, а не ту, с которой реально
гонялся анализ.

Аналогично с per-branch counts (`/api/analyses/{jobId}/branch-stats`):
`branch_id` в `reviews` — физический (LogicalBranch.Id), это работает корректно
**пока** юзер не удалил/перегруппировал LogicalBranch. После — мы видим
`reviews.branch_id` для уже несуществующего id, и Web API в JOIN с
`LogicalBranches` отдаёт `branchName = null` (фронт показывает
«Филиал удалён»).

### Что нужно поменять

Snapshot полной конфигурации запуска на момент создания job-а:

1. EF миграция: новая таблица `analysis_job_branches`
   ```sql
   CREATE TABLE analysis_job_branches (
       analysis_job_id    UUID NOT NULL REFERENCES analysis_jobs(id) ON DELETE CASCADE,
       logical_branch_id  UUID NOT NULL,          -- физический филиал, как пришёл в команде
       logical_branch_name        TEXT NOT NULL,  -- snapshot имени (для отображения если LogicalBranch удалят)
       logical_branch_address     TEXT,
       PRIMARY KEY (analysis_job_id, logical_branch_id)
   );

   CREATE TABLE analysis_job_branch_providers (
       analysis_job_id    UUID NOT NULL,
       logical_branch_id  UUID NOT NULL,
       source             VARCHAR(50) NOT NULL,
       external_id        VARCHAR(500) NOT NULL,
       external_url       TEXT NOT NULL,
       PRIMARY KEY (analysis_job_id, logical_branch_id, source, external_id),
       FOREIGN KEY (analysis_job_id, logical_branch_id)
           REFERENCES analysis_job_branches(analysis_job_id, logical_branch_id) ON DELETE CASCADE
   );
   ```

2. `Application/Messaging/Contracts/MessagingContracts.cs`: расширить
   `StartAnalysisCommand.Branches` — каждый `BranchSpec` уже содержит `BranchId`
   (= LogicalBranchId), `Source`, `ExternalId`, `ExternalUrl`. Добавить
   опц. `LogicalBranchName`, `LogicalBranchAddress` для snapshot'а.

3. `Application/Consumers/StartAnalysisCommandConsumer.cs`: при `INSERT analysis_jobs`
   параллельно INSERT-ить в `analysis_job_branches` и
   `analysis_job_branch_providers`.

4. Новый QA-endpoint `GET /api/qa/analyses/{jobId}/branches` который отдаёт
   снэпшот:
   ```json
   {
     "branches": [
       {
         "logical_branch_id": "...",
         "name": "...",
         "address": "...",
         "providers": [
           { "source": "2gis", "external_id": "...", "external_url": "..." }
         ]
       }
     ]
   }
   ```

**В Web API:**

5. `IProcessingGatewayClient.GetJobBranchesAsync(jobId, ct)` → новый метод.

6. `AnalysesController.GetJobBranches(jobId)` → читает snapshot из PG (вместо
   `companiesApi.listGroups` из текущей Company).

7. `HistoryDetailPage.AnalysisParamsCard` переключается на новый endpoint.

8. `BranchStatsBlock` — JOIN с `analysis_job_branches.logical_branch_name` вместо
   `webapi_db.LogicalBranches`, чтобы имена не «терялись» при удалении.

### Acceptance criteria

- Запустил анализ → переименовал/удалил филиал в мастере → открыл старый анализ
  → видны исходные имена/адреса/провайдеры (с момента запуска)
- Live-monitoring: каждый цикл сохраняет тот же snapshot конфигурации → история
  изменений мониторинга видна
- Старые job'ы без snapshot (до миграции) — фронт fallback'ится на текущую
  Company (как сейчас) с пометкой «параметры могли измениться»

---

## 3. Live-мониторинг: UPSERT разметки + авторитетный new-review-count + restart-all

**Откуда запрос:** Web API → live-мониторинг (`MonitoringCycleRunner`, см. `Web/live-monitoring-plan.md`).
**Приоритет:** высокий — без этого цикл мониторинга работает, но с двумя дефектами (см. ниже).
**Дата:** 2026-05-29.

### Контекст

Реализован live-мониторинг по модели **«растущий seed-job»**: Web API каждый цикл дёргает
`POST /api/qa/parser/restart-source/{jobId}/{source}` с `dateFrom = lastCollectedAt` по одному
и тому же seed-job, PG авто-доезжает пайплайн (сбор → re-LLM на ВСЁМ наборе → overwrite
summary/recs). Web API НЕ ждёт — финализирует поллингом `GET /api/qa/analyses/{jobId}` до
терминального статуса. Это работает на существующих примитивах PG, но нужны 3 доработки.

### Что нужно поменять (в Processing-Gateway)

1. **UPSERT разметки — обновлять и старые отзывы (требование пользователя).**
   Сейчас ингест LLM-результата (`ReviewLlmResultBulkInserter`) делает
   `INSERT ... ON CONFLICT (review_id, analysis_job_id) DO NOTHING` → при повторном прогоне
   старые отзывы НЕ переразмечаются. Поменять на
   `ON CONFLICT (review_id, analysis_job_id) DO UPDATE SET overall_sentiment = EXCLUDED.overall_sentiment,
   overall_confidence = EXCLUDED.overall_confidence, aspects = EXCLUDED.aspects, processed_at = NOW()`.
   Сырьё `reviews` НЕ трогаем — там по-прежнему `ON CONFLICT (composite_key) DO NOTHING` (append-only).

2. **Авторитетный new-review-count за цикл.**
   `ParserPoller` (~ингест per source) **выбрасывает** возврат `JobReviewLinker.LinkAsync` (=
   реально новые линки в `analysis_job_reviews` для этого job-а) и `RawReviewBulkInserter.InsertAsync`.
   Захватить link-delta, сложить per source в `collection_progress[source].new_review_count`,
   отдавать его в `GET /api/qa/analyses/{jobId}` (snake_case `new_review_count`).
   *(Сейчас Web API аппроксимирует «новые» через `reviews.collected_at >= cycleStart` read-only —
   работает, но авторитет у link-delta; когда поле появится, Web API переключится на него.)*

3. **`POST /api/qa/parser/restart-all/{jobId}`** — рестарт всех источников job-а одной операцией
   с единым `dateFrom`/`dateTo`. Сейчас Web API вынужден звать `restart-source` по разу на источник
   (N HTTP-вызовов + риск гонок частичного рестарта). Тело — опц. список branches per source либо
   «как было». Возвращает map источник→task_id.

4. **Concurrency-guard на rest­art.** Сейчас `RestartSource` разрешает рестарт из `collecting`
   → второй цикл может осиротить in-flight task первого. Запретить рестарт, если job уже
   `collecting` (вернуть 409). Из `sent_to_llm`/`computing_aggregates` 409 уже есть. Web API
   трактует 409 как «пропустить цикл», не как ошибку.

### Acceptance criteria

- Повторный прогон LLM на том же job-е **обновляет** `review_llm_results` всех отзывов
  (`processed_at` свежий), сырьё `reviews` не меняется.
- `GET /api/qa/analyses/{jobId}` отдаёт `new_review_count` per source = числу реально новых линков
  за последний цикл.
- `restart-all` поднимает все источники одним вызовом; одновременный второй цикл получает 409.

---

## Доля и количество подозрительных / фейковых отзывов (для PDF-отчёта)

**Откуда запрос:** Web API → PDF-отчёт по анализу (`ReportsController` / дашборд).
**Приоритет:** низкий — раздел ТЗ временно не покрыт, ждёт разметки от LLM-команды.
**Дата:** 2026-06-04.

### Контекст

ТЗ PDF-отчёта требует раздел «доля и количество подозрительных и фейковых отзывов».
В schema 2.0 LLM **не размечает** спам/фейки: поля `fake_status` / `fake_reason_tags` /
`is_spam` / `spam_confidence` удалены миграцией `Schema_2_0` (см. таблицу изменений 1.0→2.0
в `Processing-Gateway/CLAUDE.md`), новый LLM их не отдаёт. Данных нет ни в `review_llm_results`,
ни в `output_*.json`. Поэтому в PDF раздел **сейчас опущен** (согласовано с пользователем).

### Что нужно (когда LLM вернёт разметку)

1. **LLM-команда** (вне нашего кода): вернуть per-review признак спама/фейка в `output_reviews.json`
   (например `is_suspicious: bool` + `suspicion_confidence` либо аналог), bump `schema_version`.
2. **PG:** замапить новое поле в `review_llm_results` (новая колонка), отдавать его в read-модели,
   доступной `analytics_reader` (Web API Analytics читает оттуда).
3. **Web API:** добавить метрику/агрегат «подозрительные/фейковые» и вернуть раздел в PDF
   (`ReportDataAssembler` + `ReportPdfDocument`) и, при желании, карточку на дашборде.

### Acceptance criteria

- В `output_reviews.json` (schema > 2.0) есть признак подозрительности per review.
- `analytics_reader` видит колонку; Web API считает долю+количество за период/фильтры.
- PDF-отчёт показывает раздел с реальными числами вместо текущего пропуска.
