# Parser Service

Stateless REST-сервис сбора отзывов из внешних источников (2GIS, Яндекс.Карты, Google Maps).
Плагинная архитектура: каждый источник — изолированный плагин, общая антибот-инфраструктура.

> **Запросы на улучшения из соседних сервисов** (Web API, Processing Gateway) —
> в [`parser-todo.md`](parser-todo.md) в корне репы. Прежде чем планировать
> работу или брать таску — проверь, нет ли уже описанного запроса там.

## Стек

- C# / ASP.NET Core (.NET 9), controllers
- EF Core + SQLite (статусы задач)
- AWSSDK.S3 (MinIO для dev)
- Microsoft.Playwright (браузерная автоматизация)
- xUnit + WebApplicationFactory (интеграционные тесты)

## Команды

```bash
dotnet run --project src/ParserService            # Запуск
dotnet test                                        # Все тесты (кроме Docker)
dotnet test --filter "Category=Integration"        # S3-тесты (нужен Docker)
docker compose up --build                          # Dev: ParserService + MinIO
```

## Контракт плагина

Файл: `src/ParserService/Core/IReviewSourcePlugin.cs`

```csharp
public interface IReviewSourcePlugin
{
    SourceType Source { get; }
    Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(CompanySearchRequest request, CancellationToken ct);
    Task<IReadOnlyList<RawReview>> FetchReviewsAsync(BranchTarget branch, DateRange period, CancellationToken ct);
}
```

Входные типы:
- `BranchTarget(Guid BranchId, string ExternalId, string ExternalUrl)` — ExternalId для Яндекса = `businessId`
- `DateRange(DateTimeOffset From, DateTimeOffset To)` — `From` = `date_from` из запроса (или `DateTimeOffset.MinValue`), `To` = `date_to` (или `UtcNow`)
- `CompanySearchRequest(string Query, string? City, SourceType[] Sources)`

Выходные типы:
- `RawReview(string ExternalId, string Text, DateTimeOffset Date, int Stars, Guid BranchId, string? AuthorName, string? AuthorPublicId, string? TextLanguage)`
- `SearchBranchResult(SourceType Source, string ExternalId, string ExternalUrl, string Name, string Address, double? Rating, int? ReviewCount)`

Slug-маппинг SourceType: `"yandex"` <-> `SourceType.YandexMaps` (см. `Core/Models/SourceType.cs`)

## Инфраструктурные абстракции

Плагин получает через DI:

| Интерфейс | Файл | Назначение |
|-----------|------|-----------|
| `IBrowserPool` | `Infrastructure/Browser/IBrowserPool.cs` | `AcquireAsync(BrowserAcquireOptions?, ct) → object`, `ReleaseAsync(object)` — пул Playwright-контекстов. `BrowserAcquireOptions(ProxyInfo? Proxy)` — расширяемый options-объект |
| `IProxyRotator` | `Infrastructure/Proxy/IProxyRotator.cs` | `GetProxyAsync(source, ct) → ProxyInfo?`, `ReleaseProxyAsync(proxy)` |
| `IStealthConfigurator` | `Infrastructure/Stealth/IStealthConfigurator.cs` | `ApplyStealthAsync(browserContext, ct)` |
| `IPerSourceRateLimiter` | `Infrastructure/RateLimiting/IPerSourceRateLimiter.cs` | `WaitAsync(source, ct)` |

Рабочие реализации зарегистрированы в DI (`Program.cs`): `PlaywrightBrowserPool`, `ConfigProxyRotator`, `PlaywrightStealthConfigurator`, `PerSourceRateLimiter`. Stub-реализации остаются для тестов.

## Оркестрация (как вызывается плагин)

`CollectionTaskOrchestrator.ExecuteCollectionAsync`:
1. Читает задачу из SQLite
2. Десериализует `BranchesJson` → `List<BranchTargetDto>`
3. Для каждого branch вызывает `plugin.FetchReviewsAsync(target, period, ct)`
4. Обновляет `Progress` после каждого branch
5. По завершении загружает результат в S3, ставит статус `completed`
6. При исключении — статус `failed`, `Error = ex.Message`

---

## Логирование и Correlation ID (ADR-008)

Единая модель трейсинга платформы (см. корневой `../logging-trace-plan.md`): **первичный
сквозной трейс на анализ = `AnalysisJobId`** (он же `CollectionTask.JobId`); фильтр в Seq по нему
показывает весь анализ через Web API + PG + Parser. Имена свойств `LogContext` едины между
сервисами.

- Serilog → Seq (`Seq:ServerUrl` + опц. `Seq:ApiKey`). Enricher: `Service = "parser-service"`,
  `MachineName` (через `WithProperty(Environment.MachineName)` — без пакета), `Environment`.
  Console-шаблон содержит `[{CorrelationId}]`.
- `CorrelationIdMiddleware` (`Infrastructure/Telemetry/`) ловит `X-Correlation-ID`, который PG
  шлёт на `POST /api/collection-tasks` и `GET .../{taskId}` (иначе генерит Guid "N"), кладёт в
  ответ + `LogContext`. `UseSerilogRequestLogging` ставит `Direction=incoming` + уровни по статусу.
- **Фоновый сбор — особый случай.** `CollectionTaskBackgroundService` гоняет
  `CollectionTaskOrchestrator.ExecuteCollectionAsync` ВНЕ HTTP-запроса (через in-memory
  `TaskQueue`), `LogContext` (AsyncLocal) границу канала не переходит, и `X-Correlation-ID`
  исходного POST туда не доходит. Поэтому в начале `ExecuteCollectionAsync` трейс **восстанавливается
  из персистентных полей `CollectionTask`**: `AnalysisJobId`(=`JobId`), `CompanyId`, `Source`,
  `TaskId`, `Initiator="system:parser-collection"`. Все строки плагинов внутри наследуют их без правок.

---

# Задача: YandexMapsPlugin

Спецификации:
- `requirements/yandex-maps-parser for plugin.pdf` — полная спецификация плагина
- `requirements/yandex-anti-block-research.pdf` — стратегия обхода защиты

Реализация: `src/ParserService/Sources/YandexMaps/YandexMapsPlugin.cs` — `FetchReviews` работает в двух режимах (BrowserScroll / Api), `SearchBranches` парсит результаты поиска.

## Метод сбора: внутренний API (Вариант A)

У Яндекс.Карт **нет** официального API для отзывов. Используем недокументированный внутренний endpoint, который использует сам сайт.

**Endpoint:** `GET https://yandex.ru/maps/api/business/fetchReviews`

**Параметры запроса:**

| Параметр | Источник |
|----------|---------|
| `businessId` | `BranchTarget.ExternalId` (строка, например `"1124715036"`) |
| `csrfToken` | Парсится из HTML тега `<script class="config-view">` при заходе на страницу |
| `sessionId` | Из cookies сессии |
| `page` | Номер страницы (с 1) |
| `pageSize` | Макс. 50 |
| `ranking` | `by_time` или `by_relevance_org` |
| `s` | djb2-хеш по всем остальным query-параметрам |

### Хеш-параметр `s` (djb2)

```
function djb2(input: string) -> uint32:
    n = 5381
    for each char in input:
        n = (33 * n) XOR charCode(char)
    return n AND 0xFFFFFFFF
```

Вход — конкатенация всех query-параметров запроса (без `s`). Без правильного `s` API вернёт ошибку. Алгоритм реверс-инженерен, Яндекс может его сменить.

### Управление сессией (критично)

Каждый сбор по организации — **отдельная сессия**:

1. Открыть страницу организации в Яндекс.Картах через Playwright-браузер
2. Из HTML достать `csrfToken` (тег `<script class="config-view">`)
3. Сохранить cookies сессии и `sessionId`
4. Все запросы к `fetchReviews` делать с этим токеном, cookies и хешем `s`
5. **НЕ переиспользовать** токен между разными IP или организациями

### Лимиты и стратегия сбора

- **600 отзывов** — жёсткий лимит Яндекса на организацию, обойти нельзя
- Страницы по 50 штук, макс 12 страниц на сортировку
- **Workaround для 600 лимита:** собирать с двумя сортировками (`by_time` + `by_relevance_org`), дедуплицировать по `external_id` — захватывает больше уникальных отзывов
- Дата в ответе Яндекса — **ISO 8601 строка** (например `"2025-02-16T15:49:17.071Z"`), парсится через `DateTimeOffset.TryParse`
- Минимум 5-10 отзывов для анализа, иначе — уведомление пользователю

### Поддержка `date_from` (инкрементальный сбор)

Когда `DateRange.From` задан (`date_from` / `start_date`):
1. Сортировка `by_time` — новые отзывы первыми
2. Листаем страницы, пока не встретим отзыв с датой старше `date_from`
3. Останавливаемся — дальше листать не нужно
4. Экономия: вместо 600 отзывов за 5-15 мин — одна-две страницы за секунды

Когда `date_from` не задан — полный сбор с двумя сортировками.

### Поля для сохранения из ответа Яндекса

| Поле | Формат | Маппинг в RawReview |
|------|--------|---------------------|
| ID отзыва | string | `ExternalId` |
| Текст отзыва | string | `Text` |
| Дата публикации | ISO 8601 строка → DateTimeOffset | `Date` |
| Оценка | int 1-5 | `Stars` |
| ID филиала | string (businessId карточки) | Из `BranchTarget.BranchId` |

Дополнительные поля, маппятся в `RawReview`: имя автора (`AuthorName`), publicId автора (`AuthorPublicId`), язык отзыва (`TextLanguage`). Также в ответе есть `businessComment` (ответ бизнеса), `reactions`, `photos`, `videos` — пока не сохраняются.

## Режимы сбора (CollectionMode)

| Режим | Класс | Как работает |
|-------|-------|-------------|
| `BrowserScroll` (default) | `BrowserScrollCollector` | Открывает страницу отзывов в браузере, переключает сортировку на "По новизне", скроллит sidebar и перехватывает `fetchReviews` API-ответы через `page.Response` |
| `Api` | `YandexReviewCollector` + `YandexReviewApiClient` | Открывает страницу для получения csrf/session, затем вызывает API напрямую с вычислением хеша `s` |

### DOM-структура Яндекс.Карт (верифицировано апрель 2026)

- **Скролл-контейнер:** `div.scroll__container` (НЕ window scroll, body имеет `overflow:hidden`)
- **Сортировка:** `div.rating-ranking-view[role="button"]` → клик открывает `[role="dialog"]` → опции `div.rating-ranking-view__popup-line[role="button"]`
- **Навигация:** `WaitUntilState.DOMContentLoaded` (НЕ `NetworkIdle` — SPA долго грузится)
- **API-ответ:** обёрнут в `{ "data": { "reviews": [...], "hasMore": bool } }` — нужен `YandexFetchReviewsRoot` wrapper

## Обход антибот-защиты Яндекса

Яндекс — самая многослойная защита из трёх источников.

### Слои защиты

| Защита | Как работает |
|--------|-------------|
| SmartCaptcha | ML-капча с тремя уровнями (easy/medium/hard), ~85% распознаваемость. Срабатывает при подозрительном трафике |
| Поведенческий ML | Анализ мыши, скролла, кликов, времени на странице |
| Browser fingerprinting | Canvas, WebGL, audio context, список шрифтов, разрешение экрана |
| TLS/JA3 fingerprinting | Порядка cipher suites в TLS handshake — headless-6 паузеры отличаются от реальных |
| HTTP/2 pseudo-header order | Порядок заголовков у автоматизации отличается от Chrome |
| CSRF + хеш `s` | Токен привязан к сессии, хеш `s` вычисляется из параметров — без него API не отвечает |
| IP rate limiting | Датацентровые IP → мгновенный SmartCaptcha. Резидентные — дольше живут |

### Прокси

| Параметр | Значение |
|----------|---------|
| Тип | Резидентные или **мобильные РФ** (МТС, Билайн, Мегафон, Теле2) — мобильные получают наивысший trust |
| Ротация | **НЕ менять** IP посреди пагинации одной организации — `csrfToken` привязан к сессии |
| Между организациями | Менять IP каждые 3-5 организаций |
| Throttling | 2-5 сек между страницами пагинации, 10-30 сек между организациями |
| Макс. нагрузка | Не более 100-200 запросов/час на один IP |

### Против SmartCaptcha

- **Главная стратегия — избежание:** мобильные прокси + низкая скорость + прогрев сессии = капча не появляется
- Fallback: сервисы решения (2Captcha, Anti-Captcha)
- Альтернатива: Bright Data Browser API (решение на стороне провайдера)

### Против TLS/HTTP2 fingerprinting

Использовать реальный Chrome через Playwright: `chromium.Launch(channel: "chrome")` (не headless Chromium).
TLS fingerprint совпадает с настоящим браузером.

### Stealth-конфигурация

Применить stealth-патчи к browser context (аналог `playwright-stealth` / `puppeteer-extra-plugin-stealth`):
- WebGL, canvas fingerprint
- Audio context
- Font enumeration
- Navigator properties

### Мониторинг здоровья

| Сигнал | Проблема | Действие |
|--------|----------|----------|
| Ответ API содержит ошибку `csrf` | Токен протух или привязан к другому IP | Пересоздать сессию |
| Появилась SmartCaptcha | Обнаружена автоматизация | Снизить скорость, сменить IP, прогреть сессию заново |
| Хеш `s` перестал приниматься | Яндекс сменил алгоритм | Нужен реверс нового хеша |
| Кол-во отзывов меньше ожидаемого | Тихая фильтрация или лимит 600 | Логировать, проверить выборки |
| Success rate < 90% | Деградация сбора | Менять прокси, увеличить паузы |

### Обработка ошибок

- Retry: до 3 раз на transient-ошибки (сеть, таймаут, 5xx)
- При невозможности собрать — бросить исключение (оркестратор поставит `failed`)
- Частичный сбор допустим: вернуть то, что собрали (оркестратор запишет `completed` с фактическим `review_count`)
