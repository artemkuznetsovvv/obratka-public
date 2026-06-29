# ADR-010: Observability — метрики, трейсинг, алерты (post-MVP)

**НЕ входит в MVP (?):** *(реализуется после MVP; сервисы проектируются с поддержкой с нуля)*

## Context

После MVP потребуются:
- **SLO-метрики**: количество задач по статусам, среднее время прохождения флоу по этапам
- **Алерты**: автоматическое уведомление при деградации (задачи зависли, LLM недоступен, ошибки парсинга)
- **Grafana-дашборды**: визуализация health системы в реальном времени

**Что уже есть (ADR-008):** Seq + Serilog + correlation ID — покрывают debugging и
аудит событий. Но Seq не предназначен для агрегированных метрик и алертов по порогам.

**Ключевое требование к MVP-реализации:** сервисы должны быть инструментированы
с первого дня, даже если Grafana не поднята. Добавление инструментации
post-factum в production-сервис — дорого. Поднятие инфраструктуры (Prometheus + Grafana) —
дёшево и делается в любой момент.

## Decision

### OpenTelemetry — единая точка инструментации

**OpenTelemetry (OTel)** — vendor-neutral стандарт для метрик, трейсов и логов.
.NET 8 имеет native OTel support через `System.Diagnostics`.

```
NuGet (каждый микросервис):
  OpenTelemetry.Extensions.Hosting
  OpenTelemetry.Instrumentation.AspNetCore
  OpenTelemetry.Instrumentation.Http
  OpenTelemetry.Instrumentation.Runtime
  OpenTelemetry.Exporter.Prometheus.AspNetCore   ← /metrics endpoint для Prometheus
```

```csharp
// Program.cs — шаблон для всех сервисов (меняется только ServiceName)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ProcessingGateway"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()         // HTTP request duration, count, errors
        .AddHttpClientInstrumentation()         // исходящие HTTP вызовы (Parser, LLM)
        .AddRuntimeInstrumentation()            // GC, thread pool, memory
        .AddMeter("Obratka.ProcessingGateway")  // кастомные бизнес-метрики
        .AddPrometheusExporter());              // /metrics endpoint для Prometheus scrape

// /metrics endpoint (только internal — не открывать наружу)
app.MapPrometheusScrapingEndpoint("/metrics");
```

### Инфраструктура (docker-compose, post-MVP)

```yaml
prometheus:
  image: prom/prometheus:latest
  volumes:
    - ./prometheus.yml:/etc/prometheus/prometheus.yml
    - prometheus_data:/prometheus
  ports:
    - "9090:9090"

grafana:
  image: grafana/grafana:latest
  ports:
    - "3001:3000"   # не конфликтует с фронтом на 3000
  volumes:
    - grafana_data:/var/lib/grafana
  environment:
    GF_SECURITY_ADMIN_PASSWORD: "${GRAFANA_ADMIN_PASSWORD}"
```

```yaml
# prometheus.yml — scrape каждые 15 сек
scrape_configs:
  - job_name: web-api
    static_configs:
      - targets: ['web-api:8080']
        labels: { service: 'web-api' }
  - job_name: processing-gateway
    static_configs:
      - targets: ['processing-gateway:8080']
  - job_name: analytics-service
    static_configs:
      - targets: ['analytics-service:8080']
  - job_name: parser-service
    static_configs:
      - targets: ['parser-service:8080']
  - job_name: report-service
    static_configs:
      - targets: ['report-service:8080']
  - job_name: notification-service
    static_configs:
      - targets: ['notification-service:8080']
```

### Бизнес-метрики по сервисам

#### Processing Gateway — главный источник SLO-метрик

```csharp
public class AnalysisMetrics
{
    private readonly Counter<long> _jobsStarted;
    private readonly Counter<long> _jobsCompleted;
    private readonly Counter<long> _jobsFailed;
    private readonly Histogram<double> _jobDurationSeconds;
    private readonly ObservableGauge<int> _jobsInProgress;

    public AnalysisMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Obratka.ProcessingGateway");

        _jobsStarted = meter.CreateCounter<long>(
            "analysis_jobs_started_total",
            description: "Всего запущено анализов");

        _jobsCompleted = meter.CreateCounter<long>(
            "analysis_jobs_completed_total",
            description: "Завершённые анализы",
            unit: "{job}");

        _jobsFailed = meter.CreateCounter<long>(
            "analysis_jobs_failed_total");

        _jobDurationSeconds = meter.CreateHistogram<double>(
            "analysis_job_duration_seconds",
            description: "Время выполнения анализа от старта до агрегации",
            unit: "s");

        _jobsInProgress = meter.CreateObservableGauge<int>(
            "analysis_jobs_in_progress",
            () => GetActiveJobsCount());
    }

    public void RecordJobStarted(string trigger) =>      // trigger: 'user' | 'monitoring'
        _jobsStarted.Add(1, new("trigger", trigger));

    public void RecordJobCompleted(string status, TimeSpan duration) =>
        // status: 'success' | 'partial' | 'failed'
        _jobsCompleted.Add(1, new("status", status));
        _jobDurationSeconds.Record(duration.TotalSeconds, new("status", status));

    public void RecordJobFailed(string reason) =>        // reason: 'llm_timeout' | 'parser_error' | ...
        _jobsFailed.Add(1, new("reason", reason));
}
```

#### Parser Service

```csharp
// Meter: "Obratka.ParserService"
"parser_collection_tasks_total"          // counter, labels: source, status
"parser_collection_duration_seconds"     // histogram, labels: source
"parser_reviews_scraped_total"           // counter, labels: source
"parser_retry_attempts_total"            // counter, labels: source, attempt_number
```

#### [Module] Analytics (Web API)

```csharp
// Meter: "Obratka.WebApi.Analytics"
"analytics_aggregation_duration_seconds" // histogram: время пересчёта агрегатов
"analytics_events_processed_total"       // counter, labels: event_type
```

#### [Module] Reports (Web API)

```csharp
// Meter: "Obratka.WebApi.Reports"
"report_generation_duration_seconds"     // histogram: время генерации PDF
"report_generation_total"                // counter, labels: status, trigger (user|weekly)
```

### Ключевые Grafana-дашборды (примерный состав)

**Дашборд: Analysis Pipeline Health**

| Панель | Метрика | Тип |
|--------|---------|-----|
| Задачи в работе прямо сейчас | `analysis_jobs_in_progress` | Gauge |
| Задачи по статусам за 24ч | `analysis_jobs_completed_total` by status | Bar chart |
| Среднее время анализа (p50/p95/p99) | `analysis_job_duration_seconds` | Heatmap |
| Error rate (% failed) | `rate(failed) / rate(started)` | Time series |
| Парсинг по источникам | `parser_collection_tasks_total` by source | Time series |
| Retry-шторм | `parser_retry_attempts_total` | Time series |

**Дашборд: Service Health**

| Панель | Метрика |
|--------|---------|
| HTTP request rate per service | `http_server_request_duration_seconds_count` |
| HTTP error rate (5xx) | by status_code label |
| Request latency p95 | `http_server_request_duration_seconds` |
| GC pause time | `process_runtime_dotnet_gc_pause_ratio` |

### Алерты (примерные правила для Grafana Alerting)

```yaml
# Задача висит в in_progress дольше 2 часов
alert: AnalysisJobStuck
expr: analysis_jobs_in_progress > 0
  AND time() - analysis_job_last_started_at > 7200
severity: warning

# Error rate > 10% за последние 15 минут
alert: HighAnalysisFailureRate
expr: rate(analysis_jobs_failed_total[15m])
    / rate(analysis_jobs_started_total[15m]) > 0.1
severity: critical

# Parser retry-шторм
alert: ParserRetryStorm
expr: rate(parser_retry_attempts_total[5m]) > 10
severity: warning
```

### Связь с Seq-логами (ADR-008)

- **Seq** → отдельные structured log events, поиск по correlationId, debugging
- **Prometheus + Grafana** → агрегированные метрики, тренды, алерты по порогам

Это дополняющие инструменты, не конкурирующие. CorrelationId из Seq можно использовать
для drill-down: увидел аномалию на Grafana-графике → идёшь в Seq искать конкретные события.

**Путь к distributed tracing (если понадобится позже):**
OTel SDK уже подключён. Добавить `WithTracing(...)` + любой OTLP-совместимый backend
(Grafana Tempo, Jaeger) — без изменения бизнес-кода.

### Что нужно сделать при реализации каждого сервиса (сейчас, в MVP)

1. Добавить NuGet-пакеты OpenTelemetry (список выше)
2. Добавить шаблонную конфигурацию в `Program.cs`
3. Создать класс `*Metrics` с бизнес-счётчиками (пустые if не нужны сразу)
4. Зарегистрировать его как singleton и инжектировать там, где нужно
5. **Не поднимать** Prometheus/Grafana до post-MVP — `/metrics` endpoint просто будет ждать

Overhead инструментации без активного scrape ≈ ноль.

## Consequences

**Плюсы:**
- OpenTelemetry — vendor-neutral: можно поменять Jaeger на Grafana Tempo,
  Prometheus на Victoria Metrics — без изменения кода сервисов
- Инструментация в MVP → данные есть с первого дня production
- `/metrics` endpoint без scrape = нулевой overhead
- Grafana подключается к Prometheus и Jaeger одновременно — единый UI
- OTel + Serilog/Seq = correlation ID связывает метрики, трейсы, логи

**Минусы / риски:**
- Prometheus хранит метрики in-memory + WAL: при рестарте — потеря данных за несколько минут.
  Mitigation: remote write в Grafana Cloud или Victoria Metrics при необходимости долгосрочного хранения
- Кастомные бизнес-метрики нужно проектировать заранее — добавить новую метрику позже
  означает потерю исторических данных до момента добавления

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Retention метрик в Prometheus (по умолчанию 15 дней) — достаточно или нужен remote storage | При настройке |
| SLO-бюджеты: конкретные пороги (p95 < X сек, error rate < Y%) | После первых недель production |
| Alerting: Grafana Alerting vs Alertmanager | При настройке |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Только Seq для метрик | Seq — лог-агрегатор, не time-series DB; агрегированные метрики и алерты по порогам — не его задача |
| Datadog / New Relic (SaaS APM) | Vendor lock-in + цена ($$$); OTel позволяет экспортировать в них же при необходимости |
| Только Application Insights (Azure) | Привязка к Azure; OTel поддерживает AI как один из exporters |
| StatsD вместо OTel | Устаревший подход; OTel — современный стандарт с native .NET 8 support |
