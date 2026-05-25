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
