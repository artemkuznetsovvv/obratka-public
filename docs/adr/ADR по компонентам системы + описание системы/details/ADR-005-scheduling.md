# ADR-005: Планировщик задач — live-мониторинг и регулярные отчёты

## Context

Системе нужен планировщик для двух задач:

1. **Live-мониторинг** — каждая мониторинговая конфигурация пользователя имеет
   своё расписание (ежедневно / еженедельно / произвольный cron). По расписанию
   запускается цикл сбора и анализа только новых отзывов.

2. **Регулярные PDF-отчёты** — еженедельная генерация и доставка PDF-отчёта
   пользователям с активным мониторингом.

**Требования к решению:**
- Расписание конфигурируется пользователем и хранится в БД
- Jobs переживают рестарт сервиса (персистентное хранение)
- Retry при сбое запуска
- Пауза / возобновление мониторинга без рестарта
- Один Hangfire Dashboard для наблюдаемости
- Стек: .NET 8, PostgreSQL уже в системе

**Важно: планировщик не выполняет работу сам.**
Его единственная ответственность — вовремя опубликовать команду в брокер.
Реальную работу делают Processing Gateway, Reports-модуль (Web API) и т.д.

## Decision

### Hangfire

**Почему Hangfire, а не Quartz.NET:**
- Более простой API: `RecurringJob.AddOrUpdate(...)` vs XML/FluentAPI конфигурация Quartz
- Встроенный Dashboard UI без дополнительной настройки
- Нативный retry, dead-letter, наблюдаемость — из коробки
- Quartz.NET мощнее (calendars, job chaining, clustering), но эта мощь нам не нужна:
  наши jobs — простые cron-триггеры, публикующие одно сообщение в брокер
- Оба варианта зрелые для .NET, но Hangfire требует меньше кода для нашего сценария

### Storage: существующий PostgreSQL-инстанс

Hangfire создаёт свои таблицы (`HangfireJob`, `HangfireState` и др.) в том же
PostgreSQL-инстансе Web API (`webapi_db`). Processing Gateway использует отдельный инстанс (`processing_db`) — ADR-011.

**Почему не отдельная БД / Redis:**
- Единицы одновременно активных jobs на MVP — PostgreSQL справляется без Redis
- Ноль новой инфраструктуры
- Jobs — операционные данные, не бизнес-данные: совместное размещение не создаёт проблем
- Redis добавляет смысл при тысячах jobs в секунду; наш пик — десятки jobs в сутки

### Hangfire Server: внутри Web API процесса

`BackgroundJobServer` стартует вместе с Web API как hosted service:

```csharp
// Program.cs (Web API)
builder.Services.AddHangfire(config =>
    config.UsePostgreSqlStorage(connectionString));
builder.Services.AddHangfireServer();
```

**Почему не отдельный сервис:**
- Web API уже управляет жизненным циклом мониторингов (создание, пауза, удаление)
- Нет смысла в отдельном деплое при MVP-нагрузке
- Hangfire Server — это просто фоновый поток, не отдельный процесс

**Путь к извлечению (при росте нагрузки):**

```
Сейчас:
  Web API process
  ├── ASP.NET Core (HTTP)
  └── Hangfire BackgroundJobServer (фоновый поток)

Будущее (без изменения логики jobs):
  Web API process
  └── ASP.NET Core (HTTP)
      └── RecurringJob.AddOrUpdate(...)  ← только клиент, не сервер

  SchedulerService (новый процесс)
  └── Hangfire BackgroundJobServer
```

Hangfire клиент и сервер общаются через PostgreSQL-таблицы — не через HTTP.
Извлечение = перенести `AddHangfireServer()` в новый проект, убрать из Web API.
Логика jobs (публикация команд в брокер) не меняется.

### Что планирует Hangfire

#### 1. Мониторинговый цикл

```csharp
// При создании мониторинга пользователем
RecurringJob.AddOrUpdate(
    recurringJobId: $"monitoring-{monitoringId}",
    methodCall: () => _monitoringScheduler.TriggerAsync(monitoringId),
    cronExpression: monitoring.CronSchedule  // "0 9 * * 1" — понедельник 9:00
);

// При паузе мониторинга
RecurringJob.RemoveIfExists($"monitoring-{monitoringId}");

// При возобновлении — снова AddOrUpdate
```

```csharp
// Метод job — читает данные из Web API БД, собирает полную команду
public async Task TriggerAsync(Guid monitoringId)
{
    // Hangfire живёт внутри Web API → прямой доступ к Web API БД, нет cross-service read
    var config   = await _db.MonitoringConfigs
                            .Include(m => m.Company)
                            .ThenInclude(c => c.Branches)
                            .FirstAsync(m => m.Id == monitoringId);

    var correlationId = Guid.NewGuid().ToString("N"); // новый ID на каждый запуск

    using var _ = LogContext.PushProperty("CorrelationId", correlationId);
    using var __ = LogContext.PushProperty("MonitoringId", monitoringId);

    await _publisher.Publish(new StartMonitoringCycleCommand
    {
        MonitoringId = monitoringId,
        CompanyId    = config.CompanyId,
        UserId       = config.UserId,
        PeriodFrom   = config.LastRunAt ?? config.CreatedAt,
        PeriodTo     = DateTime.UtcNow,
        Branches     = config.Company.Branches
                           .Where(b => b.IsActive && config.BranchIds.Contains(b.Id))
                           .Select(b => new BranchTarget {
                               BranchId    = b.Id,
                               Source      = b.Source,
                               ExternalId  = b.ExternalId,
                               ExternalUrl = b.ExternalUrl
                           }).ToList()
    }, ctx => ctx.CorrelationId = Guid.Parse(correlationId));
}
```

Hangfire публикует `StartMonitoringCycleCommand` со **всеми необходимыми данными** в брокер.
Processing Gateway потребляет команду и начинает цикл сбора (ADR-001, ADR-004) —
без обращения к БД Web API.

#### 2. Еженедельный PDF-отчёт

```csharp
RecurringJob.AddOrUpdate(
    recurringJobId: $"weekly-report-{monitoringId}",
    methodCall: () => _publisher.Publish(new GenerateWeeklyReportCommand(monitoringId)),
    cronExpression: "0 8 * * 1"  // понедельник 8:00 (настраиваемо)
);
```

Reports-модуль (Web API) генерирует PDF (прямой вызов `IReportsModule`), Notifications-модуль доставляет в Telegram.

### Хранение расписания мониторингов

Конфигурация мониторинга хранится в таблице Web API:

```sql
CREATE TABLE monitoring_configs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    user_id         UUID NOT NULL,
    sources         JSONB NOT NULL,        -- ["2gis", "yandex"]
    branch_ids      JSONB NOT NULL,
    cron_schedule   VARCHAR(100) NOT NULL,  -- cron-выражение
    status          VARCHAR(20) NOT NULL,   -- 'active', 'paused', 'error'
    last_run_at     TIMESTAMPTZ,
    last_run_status VARCHAR(20),            -- 'success', 'partial', 'failed'
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

При изменении `cron_schedule` или `status` → Web API синхронно обновляет Hangfire job.

### Hangfire Dashboard

```csharp
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireAdminAuthFilter()]  // только администратор
});
```

Dashboard показывает все jobs, их статусы, историю выполнений, ошибки — без
дополнительного инструментария. Доступ ограничен ролью Admin.

### Сквозной поток: срабатывание мониторинга

```
Hangfire (cron trigger, Web API процесс):
  → читает monitoring_configs + company_branches из Web API БД
  → определяет period_from = last_run_at, period_to = NOW()
  → publish StartMonitoringCycleCommand {
        monitoringId, companyId, userId,
        periodFrom, periodTo,
        branches: [{ branchId, source, externalId, externalUrl }]
    }

Processing Gateway:
  → consume StartMonitoringCycleCommand
  → всё необходимое уже в команде, БД Web API не читается
  → POST /collection-tasks к Parser per source (ADR-001)
  → polling → сохранить отзывы → запустить LLM pipeline (ADR-004)
  → publish MonitoringCycleCompletedEvent { monitoringId, status, new_review_count }

Web API (MassTransit consumer):
  → consume MonitoringCycleCompletedEvent
  → UPDATE monitoring_configs SET last_run_at = NOW(), last_run_status = status
  → [Module] Notifications.SendMonitoringCycleResultAsync(...)
    → Telegram-уведомление пользователю (ADR-011)
```

## Consequences

**Плюсы:**
- Простой API: одна строка на регистрацию/обновление/удаление job
- Персистентность в существующем PostgreSQL — ноль новой инфраструктуры
- Hangfire Dashboard из коробки: наблюдаемость без Grafana/Kibana для jobs
- Retry и dead-letter jobs без дополнительного кода
- Hangfire Server в Web API процессе: один деплой вместо двух
- Путь к извлечению задокументирован и не требует изменения логики jobs

**Минусы / риски:**
- Hangfire Server в Web API: при рестарте API прерываются текущие jobs.
  Приемлемо: jobs только публикуют команду в брокер (быстро), не выполняют
  длительную работу сами
- При масштабировании Web API (несколько реплик) нужен distributed lock
  для Hangfire Server — стандартная конфигурация (`UseRecommendedRetryPolicy`)
- PostgreSQL как Hangfire storage: при пиковой нагрузке (тысячи jobs/сек)
  уступает Redis. Наш пик — десятки jobs/сутки, разницы нет

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Минимальный интервал расписания пользователя (раз в час? раз в день?) | При проектировании UI мониторинга |
| Timezone handling: cron в UTC или в timezone пользователя | При реализации |
| Retry политика для failed monitoring cycle (сразу? через час?) | При реализации |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Quartz.NET | Более сложная конфигурация без выигрыша для нашего сценария; нет встроенного Dashboard |
| MassTransit scheduled messages | Подходит для отложенных разовых задач, не для повторяющихся cron-расписаний |
| Hangfire + Redis storage | Redis — лишний компонент при десятках jobs/сутки; PostgreSQL справляется |
| Отдельный SchedulerService с нуля | Преждевременно; Hangfire in-process решает задачу с путём к выносу |
