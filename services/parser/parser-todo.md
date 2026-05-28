# Parser-Service · TODO

Реестр улучшений парсера, инициированных из соседних сервисов (Web API, Processing Gateway,
UI). Каждый пункт самодостаточный: описывает кто хочет, зачем и что конкретно поменять.

---

## 1. Per-branch progress в collection task'е

**Откуда запрос:** Web API → HistoryDetailPage (`/history/:jobId`).
**Приоритет:** низкий — UX-улучшение, ничего не блокирует.
**Дата:** 2026-05-26.

### Контекст

Сейчас `ParserCollectionTaskStatusResponse` отдаёт `progress` (`double` 0..1
или `int` 0..100, в зависимости от слоя — в Web API DTO `int`, см. ниже) на
уровне всего task'а. Один task = один источник, но в нём может быть N филиалов
(`branches: List<ParserBranchTarget>`). Параметр `progress` агрегирует прогресс
по всем филиалам в одно число.

Web API на детальной странице анализа показывает прогресс per source
(2ГИС/Яндекс/Google) — рисуется из `analysis_jobs.collection_progress`
(JSONB, ключ = source slug, значение = `{ status, progress, reviewCount, ... }`).

Хочется уметь визуализировать **внутри** одного источника:
- какие филиалы уже завершены / в процессе / упали
- сколько отзывов собрано на каждом филиале

### Что нужно поменять

**В Parser-Service:**

1. Расширить `Core/Models/CollectionTask.cs`: добавить поле
   `BranchesProgressJson` (JSONB в SQLite) — массив элементов:
   ```csharp
   public record BranchProgress(
       Guid BranchId,
       string Status,           // pending | running | completed | failed
       int? ReviewCount,
       int? ProgressPct,        // 0..100, опц. — плагин может не уметь оценивать
       string? Error);
   ```

2. Расширить `Api/Contracts/CollectionTaskStatusResponse.cs`:
   ```csharp
   public sealed record ParserBranchProgress(
       Guid BranchId, string Status, int? ReviewCount, int? ProgressPct, string? Error);

   public sealed record CollectionTaskStatusResponse(
       Guid TaskId, string Status, string Source, double Progress, int? ReviewCount,
       string? S3Url, string? Error,
       IReadOnlyList<ParserBranchProgress> Branches);
   ```

3. В `Core/CollectionTaskOrchestrator.ExecuteCollectionAsync` — обновлять
   `BranchesProgressJson` после обработки каждого branch'а:
   ```csharp
   // until/after FetchReviewsAsync per branch
   branchesProgress[i] = new BranchProgress(target.BranchId, "running", null, null, null);
   await taskRepository.UpdateProgressAsync(taskId, branchesProgress);

   // after collected:
   branchesProgress[i] = new BranchProgress(
       target.BranchId, "completed", collected.Count, 100, null);
   ```

4. Миграция SQLite: добавить колонку `BranchesProgressJson TEXT NULL` к `collection_tasks`.

**В Processing Gateway:**

5. `RawCollectionEntry` в `Application/Polling/ParserPoller.cs` (или где
   парсится ответ) — десериализовать новое поле и сохранить в
   `analysis_jobs.collection_progress[source].branches`.

6. `ProcessingDbContext` миграция: расширить структуру JSONB или просто
   принять что хранится произвольный объект с подмассивом.

**В Web API:**

7. `CollectionProgressDto` (`Integration/ProcessingGateway/Contracts/AnalysisJobContracts.cs`)
   расширить `IReadOnlyList<BranchProgressDto> Branches`.

8. `RawCollectionEntry` распарсить новое поле в маппинге.

**На фронте (Web/frontend):**

9. `api/admin.ts` → `CollectionProgressEntry` расширить `branches`.

10. `HistoryDetailPage.SourceProgressRow` — добавить collapsible-секцию с
    branch-level прогрессом (имя филиала из `companiesApi.listGroups` + `BranchProgressDto`).

### Acceptance criteria

- При сборе на 2ГИС с 5 филиалами видно «3 / 5» сразу после третьего
  завершённого branch'а
- Падение branch'а не валит весь task — отображается как failed только конкретный филиал
- Без изменений в Parser работает старый поведение (`Branches = []` → UI просто
  не показывает collapsible)

---

## 2. Параллельные branches внутри task'а + улучшение proxy-ротации

**Откуда запрос:** работа над throughput'ом сбора (комм. с пользователем 2026-05-28).
**Приоритет:** средний — даст ускорение при много-филиальных одиночных source-сборах.
**Дата:** 2026-05-28.

### Контекст

В multi-worker режиме (Workers:MaxConcurrent=3 по умолчанию, добавлено
2026-05-28) разные task'и одного job'а идут параллельно — yandex/2gis/google
стартуют одновременно. **Однако** branches ВНУТРИ одного task'а всё ещё идут
последовательно: `CollectionTaskOrchestrator.ExecuteCollectionAsync`
— обычный `for` по `branches` (см. строки 127+).

Для кейса «yandex × 10 филиалов» это даёт ~10 × (collect_time + 10-30 сек
inter-org delay) = всё ещё долго, даже если параллельно бегут 2gis и google.

Хочется уметь параллелить branches **в пределах source'а одного task'а**,
раздавая каждому branch свой прокси. Сейчас `MaxConcurrentOrgs = 1` per source
(в `RateLimitingOptions.SourceRateLimitOptions`), и `PerSourceRateLimiter`
сериализует через `ConcurrencySemaphore`.

### Что нужно поменять

**В Parser-Service:**

1. `CollectionTaskOrchestrator.ExecuteCollectionAsync`: заменить `for` по branches
   на `Parallel.ForEachAsync` (или `Task.WhenAll` с throttling через
   `SemaphoreSlim`). Лимит параллелизма = `MaxConcurrentOrgs` (того же source'а)
   или отдельная опция `MaxConcurrentBranchesPerTask`.

2. Прогресс обновлять атомарно: `Interlocked.Increment` на счётчик завершённых
   branches, потом UpdateAsync (вместо `task.Progress = (i+1)/N * 100`).

3. `PerSourceRateLimiter`: должен корректно работать когда AcquireOrgSlot/Release
   вызываются конкурентно из разных потоков того же source'а (сейчас да,
   `SemaphoreSlim` thread-safe). Поднять `MaxConcurrentOrgs:yandex` (etc) до
   2–3 через конфиг.

4. **Proxy-ротатор — главное узкое место.** Сейчас `DbProxyRotator` (см.
   `Infrastructure/Proxy/DbProxyRotator.cs`) — это нужно ревьюить отдельно:
   - Точно ли он отдаёт **разные** прокси разным concurrent-call'ам того же
     source'а? (если нет — два параллельных branch'а получают один IP
     и моментально ловят капчу).
   - Есть ли «прогрев» прокси? (без прогрева — мгновенный SmartCaptcha на Yandex).
   - Sticky-проксу per session (csrfToken Яндекса привязан к IP, см.
     CLAUDE.md «Управление сессией») — при параллельных branch'ах нужен
     proxy-per-branch на всё время сбора этого branch'а.
   - Аккуратная обработка `tried`/`failed` прокси при concurrent-выдаче.

5. (Опц.) `BrowserPoolOptions.MaxContexts` поднять с 6 до 2 × N_workers ×
   N_branches_parallel. Иначе воркеры будут спать на browser-семафоре.

### Acceptance criteria

- Yandex × 10 филиалов с `MaxConcurrentOrgs:yandex = 3` идёт примерно
  ⅓ от времени по сравнению с последовательным сбором.
- При параллельных branch'ах не растёт частота капч на источнике (5+ сборов
  подряд проходят чисто).
- При недостатке прокси параллелизм деградирует gracefully (а не валит сбор)
  — `IProxyRotator.GetProxyAsync` либо ждёт, либо возвращает null + плагин
  фолбэчится на «без прокси» / помечает branch failed.
