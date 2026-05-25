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
