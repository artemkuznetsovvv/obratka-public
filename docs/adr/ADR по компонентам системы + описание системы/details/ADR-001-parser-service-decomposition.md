# ADR-001: Декомпозиция и роль Parser Service

## Context

Система собирает отзывы из нескольких источников: 2GIS, Яндекс.Карты, Google Maps (РФ),
Отзовики (опционально). Каждый источник требует своего метода сбора.

**Зафиксировано:**
- Как минимум один, вероятно несколько источников используют **Playwright**
  (браузерная автоматизация со скроллом и парсингом DOM)
- Обход антибот-защиты обязателен: браузерный пул, прокси-ротация, stealth-плагины,
  rate limiting — общая инфраструктура для всех источников
- Конкретный метод сбора и стратегия обхода per source — **открытый вопрос**
  (требует research-задач)
- Переход на сторонние сервисы (Apify, Outscraper) возможен позже без смены архитектуры

**Требования к решению:**
1. Упростить MVP: один деплой, общая антибот-инфраструктура
2. Изолировать сбой одного источника от остальных
3. Сделать разбиение на отдельные сервисы в будущем безболезненным
4. Parser не хранит данные — это зона ответственности Processing Gateway

## Decision

### 1. Один Parser Service с плагинной архитектурой

Все источники — плагины одного сервиса. Граница между плагинами чистая:
каждый плагин зависит только от инжектируемых абстракций общей инфраструктуры,
не от других плагинов.

### 2. Parser Service — stateless REST-воркер

Parser **не хранит** собранные отзывы. Его единственная ответственность:
получить задачу, выполнить сбор, записать результат в S3, сообщить статус.

Владелец и хранитель сырых отзывов — **Processing Gateway**. Он же является
оркестратором всего pipeline анализа.

### 3. API Parser Service

#### Поиск карточек компании (для шага настройки)

```
POST /api/collection-tasks/search
Body: { "query": "Кофейня Уют", "city": "Санкт-Петербург", "sources": ["2gis", "yandex"] }
→ 200 OK
→ {
    "results": [
      {
        "source": "2gis",
        "external_id": "141265769348713",
        "external_url": "https://2gis.ru/spb/firm/141265769348713",
        "name": "Кофейня Уют на Ленина, 12",
        "address": "Санкт-Петербург, ул. Ленина, 12",
        "rating": 4.8,
        "review_count": 247
      }
    ]
  }
```

Пользователь выбирает нужные карточки из результатов → Web API сохраняет
`external_id` и `external_url` в таблицу `company_branches` per source.

#### Запуск сбора отзывов

```
POST /api/collection-tasks
Body: {
  "job_id": "uuid",
  "company_id": "uuid",
  "source": "2gis",
  "date_from": "2024-01-01T00:00:00Z",
  "date_to":   "2024-03-15T00:00:00Z",
  "branches": [
    {
      "branch_id":    "uuid",
      "external_id":  "70000001089606496",
      "external_url": "https://2gis.ru/spb/firm/70000001089606496"
    }
  ]
}
→ 202 Accepted
→ { "task_id": "uuid" }
```

**`external_id` — ключевой идентификатор для парсера:**
каждый плагин использует его для прямого перехода на карточку без повторного
поиска. Формат зависит от источника (`"70000001089606496"` для 2GIS,
`"org/12345"` для Яндекс Карт и т.д.).

```
GET /api/collection-tasks/{task_id}
→ { task_id, status, source, progress, review_count?, s3_url?, error? }
```

Статусы задачи: `pending` → `running` → `completed` / `failed`

При `completed`: поле `s3_url` содержит путь к результату в S3.
При `failed`: поле `error` содержит причину.

### 4. Взаимодействие Processing Gateway → Parser

PG является инициатором сбора. Запускает задачи по каждому источнику **параллельно**
(отдельный `task_id` per source, один HTTP-запрос per source):

```
PG: POST /collection-tasks { source: "2gis",   ... } → task_id_1
PG: POST /collection-tasks { source: "yandex", ... } → task_id_2
PG: POST /collection-tasks { source: "google", ... } → task_id_3
```

После запуска PG переходит в режим polling.

### 5. Polling вместо отдельного события завершения

PG периодически опрашивает Parser по каждой активной задаче:

```
GET /collection-tasks/{task_id}
→ { status: "running",   progress: 45 }   — обновляет progress bar
→ { status: "completed", s3_url: "..." }  — скачивает результат и продолжает pipeline
→ { status: "failed",    error: "..." }   — фиксирует ошибку источника
```

**Почему polling, а не polling + событие через брокер:**

Альтернатива — Parker публикует `TaskCompletedEvent` в брокер при завершении,
PG прекращает polling и реагирует на событие. Это более реактивный паттерн,
но для MVP он не нужен по следующим причинам:

1. **Временной масштаб задачи.** Сбор через Playwright занимает 15–60 минут.
   Задержка polling в 3–5 секунд — это < 0.5% от времени выполнения.
   Пользователь разницы не заметит.

2. **Parser не должен знать о брокере.** Добавление MassTransit и RabbitMQ
   в Parser ради нескольких секунд latency — это новая зависимость в сервисе,
   у которого и так сложная инфраструктура (Playwright, browser pool, proxy).

3. **Два механизма обнаружения одного факта.** Polling + событие означают
   два пути узнать о завершении. Это edge cases: что если событие пришло,
   но polling ещё показывает `running`? Когда прекращать polling?
   Pure polling — один механизм, одна точка отказа.

**Когда стоит добавить событие (пост-MVP):**
Если источники начнут возвращать данные за секунды (API вместо Playwright,
или быстрый сторонний сервис), latency gap polling станет значимым.
В этом случае Parser добавляет публикацию `TaskCompletedEvent` в брокер,
PG подписывается и прекращает polling. Изменение изолировано: плагин пишет
событие, PG добавляет subscription — остальная архитектура не меняется.

### 6. Передача результатов через S3 (claim-check)

Когда плагин завершает сбор, Parser записывает результат в S3 и обновляет
статус задачи. Данные не передаются через HTTP-ответ или брокер-сообщение.

```
S3: s3://obratka-jobs/{job_id}/raw/{source}.json
```

**Содержимое {source}.json:**
```json
{
  "task_id": "uuid",
  "job_id": "uuid",
  "source": "2gis",
  "company_id": "uuid",
  "collected_at": "2024-03-15T14:22:00Z",
  "reviews": [
    {
      "external_id": "abc123",
      "text": "Отличный сервис",
      "date": "2024-03-10T09:00:00Z",
      "stars": 5,
      "branch_id": "uuid"
    }
  ]
}
```

PG получает `completed` + `s3_url` → скачивает файл → сохраняет отзывы в свою БД
→ продолжает LLM pipeline (ADR-004).

### 7. Сквозной поток (Web API → Parser → PG → LLM)

```
Web API создаёт analysis_job
  → публикует StartAnalysisCommand в брокер

Processing Gateway получает команду
  → POST /collection-tasks (2gis)   → task_id_1
  → POST /collection-tasks (yandex) → task_id_2
  → POST /collection-tasks (google) → task_id_3
  → запускает polling loop

Parser выполняет задачи параллельно (per source)
  → сбор через плагин
  → запись результата в S3
  → статус задачи: completed + s3_url

PG (polling loop, каждые 3–5 сек per task):
  → обновляет статус в analysis_job (для progress bar Web API)
  → при completed: скачивает из S3, сохраняет в БД

PG ждёт завершения всех источников (MVP: один батч в LLM)
  → агрегирует collection_status: success / partial / failed
  → запускает LLM pipeline (ADR-004)

Web API (polling от frontend, каждые 3–5 сек):
  → GET /api/analyses/{job_id}/status
  → { stage: "collecting", sources: { "2gis": "running 60%", "yandex": "completed" } }
```

### 8. Контракт плагина (IReviewSourcePlugin)

```csharp
/// <summary>
/// Данные о карточке в конкретном источнике.
/// ExternalId — ключ для прямого доступа к карточке; формат зависит от источника:
///   2GIS:         "141265769348713"
///   Яндекс Карты: "org/12345"
///   Google Maps:  "ChIJxxx..."
/// </summary>
public record BranchTarget(
    Guid   BranchId,    // наш внутренний ID
    string ExternalId,  // ID карточки в источнике
    string ExternalUrl  // прямая ссылка (fallback или для навигации)
);

public record CompanySearchRequest(string Query, string? City, SourceType[] Sources);

public interface IReviewSourcePlugin
{
    SourceType Source { get; }

    /// <summary>Поиск карточек компании по названию. Вызывается при настройке компании.</summary>
    Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request,
        CancellationToken ct);

    /// <summary>
    /// Сбор отзывов для карточки. BranchTarget.ExternalId используется плагином
    /// для прямого парсинга без повторного поиска.
    /// </summary>
    Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch,
        DateRange period,
        CancellationToken ct);
}
```

Этот интерфейс — граница экстракции. При выносе источника в отдельный сервис:
- Плагин переезжает в новый проект as-is
- В основном сервисе остаётся proxy-реализация, делегирующая в новый сервис
- Логика плагина не меняется

### 9. Структура сервиса

```
ParserService/
├── Api/
│   └── CollectionTasksController.cs  ← POST + GET /collection-tasks
│
├── Core/
│   ├── IReviewSourcePlugin.cs
│   ├── CollectionTaskOrchestrator.cs ← диспетчеризация, агрегация статусов
│   ├── SqliteTaskRepository.cs          ← SQLite-хранилище для статусов активных задач
│   └── Models/
│
├── Sources/
│   ├── GoogleMaps/GoogleMapsPlugin.cs
│   ├── TwoGis/TwoGisPlugin.cs
│   ├── YandexMaps/YandexMapsPlugin.cs
│   └── Otzovik/OtzovikPlugin.cs      (опционально)
│
└── Infrastructure/
    ├── Browser/
    │   ├── IBrowserPool.cs
    │   └── PlaywrightBrowserPool.cs
    ├── Proxy/
    │   ├── IProxyRotator.cs
    │   └── ProxyRotator.cs
    ├── Stealth/
    │   └── StealthConfigurator.cs
    ├── RateLimiting/
    │   └── PerSourceRateLimiter.cs
    └── Storage/
        └── IS3ResultStorage.cs       ← запись результатов в S3
```

**Хранение статусов задач в Parser: SQLite**

Parser хранит статусы активных задач (`task_id → status, progress, s3_url`)
в **SQLite-файле** (`/data/tasks.db` внутри контейнера).

**Почему SQLite, а не in-memory (ConcurrentDictionary):**
- In-memory теряет все активные задачи при рестарте контейнера → PG получает
  timeout → ненужные retry и деградация прогресс-бара
- SQLite — нулевая инфраструктура (один файл), EF Core поддерживает нативно
- При рестарте Parser может возобновить обработку in-flight задач (статус `running`)
- При потере volume задачи всё равно считаются failed (PG держит timeout) —
  это edge case, а не норма; SQLite решает штатные рестарты
- Это не бизнес-данные: только операционное состояние текущих задач

### 10. Изоляция сбоев между источниками

Если один источник упал — остальные продолжают. `CollectionTaskOrchestrator`
агрегирует статусы независимо per source. PG получает `partial` collection
и решает: отправить в LLM то, что есть, или дождаться retry.

### 11. Безболезненная экстракция источника (будущее)

```
Сейчас:   PG → POST /collection-tasks → CollectionTaskOrchestrator → TwoGisPlugin
Будущее:  PG → POST /collection-tasks → TwoGisProxyPlugin → [HTTP] → TwoGisWorkerService
                                                                          └── TwoGisPlugin (тот же код)
```

## Consequences

**Плюсы:**
- Parser остаётся простым REST-сервисом без broker-зависимости
- Общая антибот-инфраструктура не дублируется между источниками
- Сбой одного источника изолирован
- Claim-check через S3 — единый паттерн с PG→LLM (ADR-004)
- Polling для прогресс-бара решает и обнаружение завершения — один механизм
- Чёткая граница экстракции: добавление/замена источника = новый плагин

**Минусы / риски:**
- Статусы задач в SQLite-файле: при потере volume задачи теряются, PG получает
  timeout → retry. Штатные рестарты контейнера состояние сохраняют
- Playwright требует отдельной установки браузеров в контейнере (`playwright install`)
- При большой нагрузке масштабируется весь сервис, не отдельный источник

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Метод сбора per source (Playwright vs API vs сторонний сервис) | По результатам research |
| Стратегия обхода антибот per source | По результатам research |
| Интервал polling PG → Parser (3с? 5с?) | При реализации |
| ~~In-memory vs SQLite для хранения статусов задач в Parser~~ | ✅ Решено: SQLite (`/data/tasks.db`) |
| Добавить TaskCompletedEvent в брокер если источники станут быстрее | При переходе на API-источники |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Parser хранит отзывы в БД | PG — координатор pipeline, он должен владеть данными; Parser должен оставаться stateless |
| Отдельный микросервис per source | Дублирование антибот-инфраструктуры; ops-оверхед не окупается на MVP |
| Broker-событие при завершении задачи | Новая зависимость в Parser при временном масштабе задач 15–60 мин; polling достаточен |
| Web API оркестрирует Parser напрямую | Web API становится fat controller, держит большие payload в памяти |
| Polling + broker-событие | Два механизма обнаружения одного факта; edge cases; избыточно при текущих временных масштабах |
