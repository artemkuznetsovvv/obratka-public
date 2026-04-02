# ADR-011: Декомпозиция сервисов для MVP — Modular Monolith

**Данный ADR рассматривает подходы:** accepted

## Context

Изначальная идеальная архитектура предполагала 6 отдельных микросервисов:
Web API, Parser Service, Processing Gateway, Analytics Service, Report Service, Notification Service.

По результатам сопоставления сроков и требования к MVP в ADR выявлено, что для MVP (10–100 компаний, 120k отзывов, команда 1–2 человека)
это создаёт непропорциональный ops-overhead:

- 11 контейнеров в docker-compose (7 сервисов + 4 инфраструктура)
- ~20+ часов на настройку инфраструктуры без бизнес-ценности
- Analytics Service, Report Service и Notification Service делают cross-service reads и не имеют
  независимых нефункциональных требований (SLA, масштабирование, команда-владелец)
- Все три сервиса уже запускались событиями из Web API / Processing Gateway

**Критерии выделения в отдельный микросервис (Для MVP):**
выносить только если выполняется хотя бы одно:
- Независимый цикл деплоя
- Разные нефункциональные требования
- Разные команды-владельцы
- Явная граница по бизнес-домену (Bounded Context)

Analytics Service, Report Service, Notification Service **не удовлетворяют** ни одному критерию на MVP.

## Decision

### Три deployable unit вместо шести

```
MVP:
  ┌─────────────────────────────────────────────────────────────┐
  │ Web API Process                                             │
  │  ├── ASP.NET Core (HTTP, BFF)                               │
  │  ├── Identity + JWT                                        │
  │  ├── Hangfire Server (scheduler)                           │
  │  ├── [Module] Analytics  ← агрегации, timeseries, KPI      │
  │  ├── [Module] Reports    ← генерация PDF (QuestPDF+Skia)   │
  │  └── [Module] Notifications ← Telegram-бот                │
  └─────────────────────────────────────────────────────────────┘

  ┌──────────────────────┐
  │ Processing Gateway   │
  │  ├── MassTransit     │
  │  ├── Parser poller   │
  │  ├── LLM pipeline    │
  │  └── HTTP status API │
  └──────────────────────┘

  ┌──────────────────────┐
  │ Parser Service       │
  │  ├── Playwright pool │
  │  ├── Proxy rotation  │
  │  └── Source plugins  │
  └──────────────────────┘
```

### Отдельные БД для Web API и Processing Gateway

| Сервис | БД-инстанс | Таблицы |
|--------|-----------|---------|
| Web API | `webapi_db` | AspNetUsers, companies, company_branches, analysis_requests, monitoring_configs, refresh_tokens, Hangfire tables, **analytics tables**, **report metadata** |
| Processing Gateway | `processing_db` | reviews, review_llm_results, analysis_jobs (включая `recommendation`) |

**Почему отдельные инстансы, а не одна БД:**
- Processing Gateway — независимо масштабируемый компонент с высокой нагрузкой на запись (вставка 1000+ отзывов за цикл)
- Web API + модули — нагрузка на чтение (дашборд, PDF), другой профиль запросов
- Чёткая граница ownership: никто не пишет в чужие таблицы
- При необходимости вынести модуль — меняем только connection string

**Analytics-модуль читает таблицы Processing Gateway при пересчёте агрегатов:**

> ⚠️ **MVP trade-off.** Analytics-модуль читает `reviews`, `review_llm_results`
> и `analysis_jobs` из `processing_db` один раз за цикл анализа (не в рантайме дашборда).
> Это **schema coupling**: изменение структуры этих таблиц в Processing Gateway
> потребует синхронного обновления `ProcessingReadContext` в Web API.
>
> Принято сознательно в рамках MVP-сроков. Риск ограничен: таблицы стабильны
> (схема зафиксирована в ADR-002, ADR-004), чтение разовое, пользователь `analytics_reader`
> ограничен тремя таблицами. Путь ликвидации задокументирован ниже.

**Конфигурация cross-service read:**

```csharp
// Program.cs (Web API)
// Основная БД Web API
builder.Services.AddDbContext<WebApiDbContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:WebApiDb"]));

// Read-only соединение с БД Processing Gateway (только для Analytics-модуля)
// MVP trade-off: schema coupling. Заменить на HTTP API или S3 claim-check при экстракции.
builder.Services.AddDbContext<ProcessingReadContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:ProcessingDb"])
           .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
```

```json
// docker-compose → environment Web API сервиса
{
  "ConnectionStrings__WebApiDb":     "Host=webapi-db;Database=webapi;Username=webapi_user;Password=...",
  "ConnectionStrings__ProcessingDb": "Host=processing-db;Database=processing;Username=analytics_reader;Password=..."
}
```

`analytics_reader` — отдельный PostgreSQL-пользователь с правами `SELECT` только
на `reviews`, `review_llm_results` и `analysis_jobs`. Без прав на INSERT / UPDATE / DELETE
и без доступа к другим таблицам `processing_db`.

**Путь ликвидации coupling (при экстракции Analytics в отдельный сервис):**

Два равнозначных варианта — выбор при экстракции:

| Вариант | Суть | Когда предпочесть |
|---------|------|-------------------|
| **HTTP API Processing Gateway** | PG добавляет `GET /internal/analyses/{jobId}/reviews` → Analytics читает по HTTP | Если PG уже развит, хочется явного контракта |
| **S3 claim-check** | PG пишет `analytics_input.json` в S3 перед публикацией события; Analytics читает из S3 | Согласованно с ADR-004; даёт аудит-лог «что пошло в агрегацию» |

Шаги при любом варианте:
1. Удалить `ProcessingReadContext` и `analytics_reader` из Web API
2. Processing Gateway реализует выбранный интерфейс
3. Analytics-модуль меняет источник данных (HTTP-клиент или S3-чтение)
4. Логика агрегации (`ComputeAggregatesAsync`) не меняется

### Граница модуля — контракт для будущей экстракции

Каждый модуль оформляется как отдельный C#-проект (class library) с публичным интерфейсом:

```csharp
// Analytics module contract
public interface IAnalyticsModule
{
    Task ComputeAggregatesAsync(Guid analysisJobId, CancellationToken ct);
    Task<DashboardSnapshot> GetLatestSnapshotAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<TimeseriesPoint>> GetTimeseriesAsync(Guid companyId, DateRange range, CancellationToken ct);
    Task<IReadOnlyList<TopicStat>> GetTopicsAsync(Guid analysisJobId, CancellationToken ct);
}

// Reports module contract
public interface IReportsModule
{
    Task<string> GenerateReportAsync(Guid analysisJobId, Guid companyId, CancellationToken ct); // возвращает S3 URL
}

// Notifications module contract
public interface INotificationsModule
{
    Task SendMonitoringCycleResultAsync(MonitoringCycleResult result, CancellationToken ct);
    Task SendReportReadyAsync(string reportUrl, Guid userId, CancellationToken ct);
    Task SendAdminAlertAsync(string message, string correlationId, CancellationToken ct);
}
```

При вызовах внутри процесса — прямой вызов интерфейса (не HTTP).
При экстракции в отдельный сервис — заменяется proxy-реализацией, делегирующей по HTTP/брокеру.
Логика модуля не меняется.

### Путь к экстракции (post-MVP)

```
Сейчас:
  Web API → IAnalyticsModule.ComputeAggregatesAsync(...)
                └── прямой вызов AnalyticsModule

После экстракции:
  Web API → IAnalyticsModule.ComputeAggregatesAsync(...)
                └── AnalyticsModuleHttpProxy → HTTP → Analytics Service
```

Шаги экстракции (на примере Analytics):
1. Вынести `AnalyticsModule` в отдельный проект `AnalyticsService`
2. Добавить ASP.NET Core host, HTTP endpoints
3. В Web API заменить регистрацию `AnalyticsModule` на `AnalyticsModuleHttpProxy`
4. Обновить docker-compose

Логика не меняется. Только транспорт.

### Коммуникация модулей внутри Web API

| Вместо | Теперь |
|--------|--------|
| `AnalysisCompletedEvent` → брокер → Analytics Service (HTTP consumer) | Processing Gateway → `AnalysisCompletedEvent { jobId, companyId }` → брокер → Web API MassTransit consumer → `IAnalyticsModule.ComputeAggregatesAsync(...)` |
| *(нет аналога в микросервисной схеме)* | Web API → `AggregatesReadyEvent { jobId, companyId }` → брокер → Processing Gateway: `UPDATE analysis_jobs SET status = 'completed'`; Processing Gateway — владелец таблицы, он обновляет финальный статус |
| `GenerateReportCommand` → брокер → Report Service | Web API: прямой вызов `IReportsModule.GenerateReportAsync(...)` |
| `MonitoringCycleCompletedEvent` → брокер → Notification Service | Web API MassTransit consumer → `INotificationsModule.SendMonitoringCycleResultAsync(...)` |

Web API по-прежнему подписан на события от Processing Gateway через брокер.
Разница: обработчик события вызывает модуль напрямую, а не через брокер.

## Consequences

**Плюсы:**
- 9 контейнеров вместо 11 (экономия ~2 контейнера; 9 = 3 сервиса + Frontend + 2×PostgreSQL + RabbitMQ + MinIO + Seq)
- ~80–120 часов экономии на инфраструктуре, debugging, ops
- Debugging в одном процессе: стектрейс сквозной, нет «где потерялось сообщение»
- Отдельные БД для Web API и Processing Gateway: чёткая граница ownership
- Модульные интерфейсы зафиксированы с первого дня — экстракция не требует рефакторинга логики

**Минусы / риски:**
- Web API процесс становится «жирнее»: HTTP + Hangfire + Analytics + Reports + Notifications
  Митигация: каждый модуль — отдельный class library, DI-контейнер изолирует зависимости
- PDF-генерация (QuestPDF + SkiaSharp) нагружает тот же процесс, что и HTTP-запросы
  Митигация: генерация PDF асинхронна (Hangfire job или MassTransit consumer), не блокирует HTTP
- При росте нагрузки масштабируется весь Web API, не отдельный модуль
  Митигация: экстракция модуля по критериям ADR-011, путь описан выше

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Все 6 микросервисов с MVP | ops-overhead на настройку инфры и реализации всяких апих уйдет половины времени разработки |
| Один монолит (все сервисы в одном процессе) | Parser Service требует Playwright + браузеры — изоляция обязательна; Processing Gateway изолирует внешний LLM-контракт |
| Analytics как отдельный сервис сразу | Не удовлетворяет ни одному критерию выделения на MVP; cross-service read остаётся в любом случае |
