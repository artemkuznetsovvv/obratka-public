# ADR-008: Централизованное логирование и трейсинг

## Context

Система состоит из трёх deployable unit (.NET 8) + Frontend:
**Parser Service**, **Processing Gateway**, **Web API** (включает модули: Analytics, Reports, Notifications).
По ADR-011 Analytics, Reports и Notifications — модули Web API, не отдельные сервисы.

**Требования:**
- Единая точка просмотра логов всех сервисов
- Correlation ID: связать одну цепочку событий «клик → сбор → LLM → отчёт» через все сервисы
- Логировать: все входящие HTTP-запросы, все исходящие (источники, LLM, Telegram),
  ключевые события фоновых job (Hangfire, MassTransit consumer)
- Каждая запись: время, сервис, correlation ID, initiator (user/job), company_id/job_id,
  направление запроса, результат, длительность
- Срок хранения: 30–90 дней
- Доступ к логам: только администратор
- Стек: .NET 8

**Ограничение:** ELK Stack (Elasticsearch + Kibana) требует 3–4 GB RAM
только для инфраструктуры логирования — избыточно для MVP.
Должен быть задокументирован путь к ELK без изменения кода сервисов.

## Decision

### Seq — единое хранилище логов для MVP

**Seq** — легковесный лог-агрегатор (.NET-first): один Docker-контейнер,
структурированные события, мощный query-язык, бесплатная лицензия для одного узла.

```yaml
# docker-compose.yml (добавляется к инфраструктурным сервисам)
seq:
  image: datalust/seq:latest
  ports:
    - "5341:80"    # Seq Web UI (admin)
    - "5342:5341"  # HTTP ingestion endpoint
  environment:
    ACCEPT_EULA: Y
  volumes:
    - seq_data:/data
  mem_limit: 512m  # Seq: ~256–512 MB vs ES: 2+ GB
```

**Почему Seq, а не ELK на MVP:**

| Критерий | Seq | ELK |
|----------|-----|-----|
| RAM | ~256–512 MB | 3–4 GB (ES + Kibana) |
| Инфраструктура | 1 контейнер | 2–3 контейнера |
| Настройка | Ноль | ILM, index templates, Logstash pipeline |
| Запросы | SQL-подобный язык | KQL (мощнее, но сложнее) |
| Serilog интеграция | Native sink | `Serilog.Sinks.Elasticsearch` |
| Лицензия | Бесплатно (1 узел) | Open source |
| Продакшн-масштаб | Ограничен одним узлом | Кластер, репликация, шардинг |

Seq решает все MVP-требования. ELK — при росте нагрузки или команды.

### Serilog — единая абстракция для всех сервисов

**Критически важно:** все сервисы используют **Serilog** как логирующий фреймворк.
Смена backend (Seq → ELK) = замена sink-пакета в одном месте.
Код сервисов не меняется.

```bash
# NuGet (общий для всех сервисов)
Serilog.AspNetCore
Serilog.Sinks.Seq
Serilog.Enrichers.CorrelationId
Serilog.Enrichers.Environment
```

**Единый шаблон конфигурации** (копируется в каждый сервис):

```csharp
// Program.cs — одинаково для всех микросервисов
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithProperty("Service", "ProcessingGateway")  // меняется per service
    .Enrich.WithCorrelationId()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Seq("http://seq:5342")  // внутренняя сеть Docker
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .CreateLogger();

builder.Host.UseSerilog();
```

Это обеспечивает структурированные события с полями `Service`, `CorrelationId`,
`MachineName` на каждой записи — без повторения в каждом `logger.Log(...)` вызове.

### Correlation ID — сквозная прослеживаемость

Каждое событие несёт один `CorrelationId` от момента инициации до завершения цепочки.

#### HTTP-сервисы (Web API, Parser Service)

```csharp
// Middleware: читает из заголовка или создаёт новый
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"]
        .FirstOrDefault() ?? Guid.NewGuid().ToString("N");

    context.Response.Headers["X-Correlation-ID"] = correlationId;

    using (LogContext.PushProperty("CorrelationId", correlationId))
    using (LogContext.PushProperty("CompanyId",
        context.Items.TryGetValue("CompanyId", out var cid) ? cid : null))
    {
        await next();
    }
});
```

Все исходящие HTTP-запросы передают заголовок дальше:

```csharp
// HttpClient в Processing Gateway → Parser Service
httpClient.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
```

#### MassTransit (брокер)

MassTransit автоматически передаёт `CorrelationId` через envelope сообщения.
Добавляем Serilog enricher на consumer:

```csharp
public class AnalysisCompletedEventConsumer : IConsumer<AnalysisCompletedEvent>
{
    public async Task Consume(ConsumeContext<AnalysisCompletedEvent> context)
    {
        using var _ = LogContext.PushProperty("CorrelationId",
            context.CorrelationId?.ToString() ?? context.MessageId?.ToString());
        using var __ = LogContext.PushProperty("AnalysisJobId",
            context.Message.AnalysisJobId);

        _logger.LogInformation("Starting aggregation for job {AnalysisJobId}",
            context.Message.AnalysisJobId);
        // ...
    }
}
```

#### Hangfire Jobs

```csharp
// При постановке job в очередь — correlationId передаётся как параметр
RecurringJob.AddOrUpdate(
    $"monitoring-{monitoringId}",
    () => _scheduler.TriggerMonitoring(monitoringId, correlationId),
    monitoring.CronSchedule
);

// В методе job
public async Task TriggerMonitoring(Guid monitoringId, string correlationId)
{
    using var _ = LogContext.PushProperty("CorrelationId", correlationId);
    using var __ = LogContext.PushProperty("MonitoringId", monitoringId);

    await _publisher.Publish(new StartMonitoringCycleCommand(monitoringId),
        ctx => ctx.CorrelationId = Guid.Parse(correlationId));
}
```

### Что и как логируется

#### Стандартные middleware-события (Serilog.AspNetCore)

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diag, context) =>
    {
        diag.Set("CompanyId", context.Items["CompanyId"]);
        diag.Set("UserId", context.User.FindFirst("sub")?.Value);
    };
});
```
Автоматически логирует: метод, путь, статус, длительность для каждого запроса.

#### Ключевые события по сервисам

| Сервис | Событие | Уровень |
|--------|---------|---------|
| Web API | Запуск анализа | Information |
| Web API | Auth failure | Warning |
| Processing Gateway | Задача Parser создана | Information |
| Processing Gateway | Отзывы сохранены в БД | Information |
| Processing Gateway | LLM pipeline запущен | Information |
| Processing Gateway | Ошибка источника (retry) | Warning |
| Processing Gateway | Превышен retry limit | Error |
| Web API [Module] Analytics | Агрегаты рассчитаны | Information |
| Web API [Module] Reports | PDF сгенерирован и загружен в S3 | Information |
| Web API [Module] Reports | Ошибка генерации PDF | Error |
| Web API [Module] Notifications | Telegram-сообщение отправлено | Information |
| Web API [Module] Notifications | Ошибка доставки | Error |
| Hangfire | Job triggered | Information |
| Hangfire | Job failed | Error |

Ошибки уровня `Error` и `Fatal` → **дополнительно** уведомление администратору в Telegram
(Notifications-модуль Web API обрабатывает соответствующие события).

### Seq Dashboard для администратора

```csharp
// Web API: проксирование Seq UI только для Admin
// Seq UI доступен по /admin/logs → reverse proxy к http://seq:5342
// ИЛИ отдельный порт Seq защищается на уровне инфраструктуры (nginx basic auth / network policy)
```

Seq предоставляет готовый UI с:
- Полнотекстовым поиском по структурированным полям
- Фильтрами: `Service = 'ProcessingGateway' AND @Level = 'Error'`
- Трейсингом: `CorrelationId = 'abc123'` → все события цепочки

### Retention (30–90 дней)

```json
// Seq: Settings → Retention Policies
{
  "retentionPolicy": {
    "maximumAge": "90.00:00:00"
  }
}
```

Seq автоматически удаляет события старше указанного срока.

## Путь к ELK Stack (без изменения кода)

Когда Seq перестаёт справляться (рост объёма, несколько узлов, команда):

**Шаг 1:** Поднять Elasticsearch + Kibana

**Шаг 2:** Заменить sink во всех сервисах:

```csharp
// Было:
.WriteTo.Seq("http://seq:5342")

// Стало:
.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri("http://elasticsearch:9200"))
{
    IndexFormat = "obratka-logs-{0:yyyy.MM}",
    AutoRegisterTemplate = true,
    NumberOfShards = 1,
    NumberOfReplicas = 1
})
```

**Шаг 3:** Настроить ILM в Kibana для 90-дневного retention.

**Больше ничего не меняется.** Структура событий (поля, correlation ID, обогатители)
остаётся идентичной — Serilog абстрагирует backend.

**Пакет:** `Serilog.Sinks.Elasticsearch` (добавляется, `Serilog.Sinks.Seq` — убирается).

### OpenTelemetry (трейсинг — следующий шаг после MVP)

Текущее решение (correlation ID через LogContext) покрывает потребности MVP.
При необходимости полного distributed tracing (spans, traces, Jaeger/Tempo):

```csharp
// Добавить после MVP
builder.Services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());  // → Jaeger, Grafana Tempo, или Elastic APM
```

Correlation ID и OpenTelemetry trace ID можно связать через enricher —
миграция не разрушает существующие Seq-запросы.

## Consequences

**Плюсы:**
- ~256–512 MB RAM vs 3–4 GB для ELK — экономия критична при MVP
- Один Docker-контейнер, нулевая настройка шаблонов/пайплайнов
- Serilog-абстракция гарантирует: смена backend = один конфиг, не рефакторинг кода
- Correlation ID покрывает основную потребность трейсинга без overhead OTEL
- Seq бесплатен для одного узла; коммерческая лицензия нужна только при кластеризации
- Retention через Seq UI без кода

**Минусы / риски:**
- Seq — single node: нет HA, нет горизонтального масштабирования. Приемлемо для MVP
- При потере Seq-контейнера — потеря ненаправленных событий (буфер в памяти Serilog).
  Митигация: `bufferBaseFilename` в конфиге sink → Serilog буферизует на диск при недоступности Seq
- Seq community edition ограничен 1 GB данных на диске (бесплатная лицензия).
  Mitigation: 90-дневный retention + настройка размера диска

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Буферизация на диск (при недоступности Seq) — добавить сразу или по мере необходимости | При реализации |
| Алерты в Seq (Signal) vs отдельный alerting через Notification Service | При реализации |
| OpenTelemetry поверх Seq или вместо — когда добавлять | После MVP, при запросе на полный distributed tracing |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| ELK Stack (Elasticsearch + Kibana) | 3–4 GB RAM только на инфраструктуру логирования — избыточно для MVP. Документирован путь перехода |
| Graylog | Требует Elasticsearch + MongoDB как зависимости — фактически тот же overhead что ELK |
| Axiom (облачный) | Внешний SaaS, данные уходят из периметра; приемлемо только если облачный деплой уже принят |
| Seq + Filebeat → Elasticsearch | Усложняет MVP без выигрыша: Filebeat добавляет агент, ES всё равно нужен |
| Только Console + stdout + docker logs | Нет поиска, нет correlation, нет структурированных запросов — неприемлемо |
