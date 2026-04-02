# ADR-003: База данных для аналитических метрик

> **Deployment context (ADR-011):** Analytics реализован как **модуль внутри Web API**, не как отдельный сервис.
> Интерфейс: `IAnalyticsModule`. Таблицы хранятся в `webapi_db`.

## Context

Analytics-модуль Web API отвечает за хранение и предоставление агрегированных метрик:
NPS, распределение тональности, топ-темы, болевые точки, динамика по периодам.

**Характеристики нагрузки:**
- Множество пользователей, у части — несколько компаний под мониторингом
- Dashboard читается часто, в том числе параллельно разными пользователями
- Пересчитывать GROUP BY по `reviews` + `review_llm_results` (120k строк) на каждый запрос —
  нецелесообразно: при росте числа пользователей и компаний это создаёт предсказуемую нагрузку
- Агрегаты пересчитываются **один раз** по завершении анализа и хранятся готовыми
- Dashboard только читает — никаких JOIN на сырых данных в рантайме

**Триггер пересчёта:**
Processing Gateway завершил LLM pipeline → публикует `AnalysisCompletedEvent` в брокер →
Analytics-модуль (Web API) потребляет событие → читает `reviews` + `review_llm_results` →
вычисляет агрегаты → сохраняет в свои таблицы.

**Что нужно от хранилища:**
- Запись агрегатов раз в N часов (по завершении цикла)
- Чтение для дашборда: простые SELECT по company_id + analysis_job_id (не GROUP BY сырых данных)
- Фильтрация: по источнику, филиалу, теме (уже встроена в агрегаты)
- Временны́е ряды для трендовых графиков: SELECT snapshots ORDER BY period_from
- Объём: несколько тысяч строк агрегатов (не 120k сырых отзывов)

**Стек:** .NET 8, EF Core, PostgreSQL уже в системе (ADR-002).

## Decision

### PostgreSQL

По ADR-011 Analytics-модуль включён в Web API процесс. Его таблицы хранятся в **`webapi_db`** —
отдельном PostgreSQL-инстансе Web API, логически изолированном от `processing_db`.

**Почему не TimescaleDB:**
TimescaleDB оптимизирован для time-series запросов по сырым данным (например,
«дай мне все отзывы с sentiment = negative за последние 30 дней»). Здесь этого нет:
мы храним уже вычисленные снапшоты, а не сырые точки. Трендовые графики —
это просто `SELECT ... ORDER BY period_from` по таблице снапшотов с десятками строк
на компанию. Никакого time-bucketing, партиционирования или continuous aggregates
на этом объёме не нужно.

Upgrade path задокументирован ниже.

**Почему не ClickHouse:**
ClickHouse — OLAP-движок для аналитики по миллиардам raw-строк без предагрегации.
Мы делаем предагрегацию сами. Dashboard читает уже готовые числа. ClickHouse
добавил бы отдельный сервис, другой протокол (.NET-драйвер менее зрелый),
и сложную операционную модель — без какого-либо выигрыша на нашем паттерне доступа.

### Две задачи — две таблицы

Агрегаты решают два разных запроса:

| Запрос | Источник данных |
|--------|----------------|
| KPI-карточки + темы за конкретный анализ | `analysis_snapshots` + `topic_stats` (per job) |
| Динамика NPS/тональности по неделям/месяцам | `metric_timeseries` (per company × week) |

Это разные единицы агрегации. Смешивать их в одной таблице нельзя: снапшот
одного analysis_job может охватывать 3 месяца, а динамика требует разбивки
внутри этих 3 месяцев по неделям.

### Схема таблиц Analytics-модуля

```sql
-- 1. Снапшот KPI за весь период analysis_job
--    Источник: KPI-карточки дашборда, сводная секция PDF-отчёта
CREATE TABLE analysis_snapshots (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id          UUID NOT NULL,
    analysis_job_id     UUID NOT NULL UNIQUE,
    period_from         TIMESTAMPTZ NOT NULL,
    period_to           TIMESTAMPTZ NOT NULL,
    review_count        INT NOT NULL,
    avg_stars           FLOAT,
    nps                 FLOAT NOT NULL,
    -- ВАЖНО: хранятся абсолютные числа (counts), НЕ проценты.
    -- Это позволяет корректно агрегировать до месяца/квартала суммированием.
    -- NPS = (positive+very_positive - negative+very_negative) / total × 100
    -- вычисляется в приложении из этих counts.
    sentiment_dist      JSONB NOT NULL,   -- {"very_negative":12,"negative":30,"neutral":45,"positive":60,"very_positive":25}
    fake_ratio          FLOAT NOT NULL,
    spam_ratio          FLOAT NOT NULL,
    recommendation      TEXT,             -- текст рекомендации от LLM (job-level, из analysis_jobs)
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_snapshots_company_period
    ON analysis_snapshots (company_id, period_from DESC);

-- 2. Недельные точки временного ряда, привязанные к дате отзыва
--    Источник: трендовые графики на дашборде (NPS/тональность/количество по неделям)
--    Ключ: (company_id, period_week) — независимо от того, каким analysis_job вычислено
--    При каждом анализе делается UPSERT для всех недель, попавших в период
CREATE TABLE metric_timeseries (
    company_id          UUID NOT NULL,
    period_week         DATE NOT NULL,    -- понедельник недели (DATE_TRUNC('week', review_date))
    source              VARCHAR(50),      -- NULL = все источники суммарно
    review_count        INT NOT NULL,
    avg_stars           FLOAT,
    nps                 FLOAT NOT NULL,
    -- Абсолютные числа (counts), НЕ проценты — обязательно для корректной
    -- агрегации до месяца/квартала: SUM(counts) даёт правильный результат,
    -- SUM(ratios) — нет. Месячный NPS вычисляется из суммы weekly counts.
    sentiment_dist      JSONB NOT NULL,
    fake_ratio          FLOAT NOT NULL,

    PRIMARY KEY (company_id, period_week, COALESCE(source, ''))
);

CREATE INDEX idx_timeseries_company_week
    ON metric_timeseries (company_id, period_week DESC);

-- 3. Разбивка по филиалам (для фильтра "филиал" на дашборде)
CREATE TABLE branch_snapshots (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    analysis_job_id     UUID NOT NULL REFERENCES analysis_snapshots(analysis_job_id),
    branch_id           UUID NOT NULL,
    source              VARCHAR(50) NOT NULL,
    review_count        INT NOT NULL,
    avg_stars           FLOAT,
    sentiment_dist      JSONB NOT NULL,
    fake_ratio          FLOAT NOT NULL,

    UNIQUE (analysis_job_id, branch_id)
);

-- 4. Топ-темы и тональность по теме (для блока "Темы и болевые точки")
--    Привязано к analysis_job: "топ-темы за этот анализ / этот период"
CREATE TABLE topic_stats (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    analysis_job_id     UUID NOT NULL REFERENCES analysis_snapshots(analysis_job_id),
    topic               VARCHAR(200) NOT NULL,
    review_count        INT NOT NULL,
    sentiment_dist      JSONB NOT NULL,   -- тональность внутри этой темы

    UNIQUE (analysis_job_id, topic)
);

CREATE INDEX idx_topic_stats_job
    ON topic_stats (analysis_job_id);

-- 5. Рекомендации
--    LLM возвращает recommendation как единую строку на уровне job (не per review).
--    Processing Gateway сохраняет её в analysis_jobs.recommendation (ADR-004).
--    Analytics-модуль копирует в analysis_snapshots.recommendation при пересчёте агрегатов.
--    Отдельная таблица не нужна.
```

### Владение данными

| Таблица | Пишет | Читает |
|---------|-------|--------|
| `analysis_snapshots` | Analytics-модуль (Web API) | Web API, Reports-модуль |
| `metric_timeseries` | Analytics-модуль (Web API) | Web API |
| `branch_snapshots` | Analytics-модуль (Web API) | Web API, Reports-модуль |
| `topic_stats` | Analytics-модуль (Web API) | Web API, Reports-модуль |

Analytics-модуль читает из `reviews`, `review_llm_results` и `analysis_jobs` (таблицы Processing Gateway,
`processing_db`) **только при пересчёте агрегатов** (один раз за цикл)
через `ProcessingReadContext` (read-only, см. раздел «Физическое размещение»).
Рекомендация (`recommendation`) читается из `analysis_jobs` — без обращения к S3.

### Фильтры дашборда и pre-computed данные

Не все фильтры дашборда можно закрыть pre-computed агрегатами. Разделение явное:

| Фильтр | Источник данных | Обоснование |
|--------|----------------|-------------|
| Период | `analysis_snapshots` + `metric_timeseries` | Pre-computed ✅ |
| Источник | `branch_snapshots.source` + `metric_timeseries.source` | Pre-computed ✅ |
| Филиал | `branch_snapshots.branch_id` | Pre-computed ✅ |
| Тема | `topic_stats.topic` | Pre-computed ✅ |
| Тональность | `review_llm_results.sentiment` (raw) | Raw data ⚠️ — см. ниже |
| Рейтинг (звёзды) | `reviews.stars` (raw) | Raw data ⚠️ — см. ниже |

**Фильтры по тональности и звёздам работают иначе:**
Они не фильтруют KPI-карточки (KPI всегда за полный срез), а применяются
к **списку отзывов** в нижней части дашборда («Топ позитивных / негативных», «Примеры»).
Analytics-модуль предоставляет данные для выборки отзывов с фильтрами (через Web API endpoint):

```sql
-- Web API: GET /api/analyses/{jobId}/reviews?sentiment=negative&stars=1,2&limit=20
SELECT r.raw_text, r.stars, r.review_date, r.source,
       llm.sentiment, llm.topics, llm.fake_status
FROM reviews r
JOIN review_llm_results llm ON llm.review_id = r.id
WHERE llm.analysis_job_id = ?
  AND llm.sentiment = ?          -- опционально
  AND r.stars = ANY(?)           -- опционально
ORDER BY r.review_date DESC
LIMIT 20;
```

Это единственное допустимое обращение к сырым данным в рантайме дашборда.
Запрос лёгкий (индекс по `analysis_job_id`, результат — десятки строк)
и не нарушает принцип: KPI и тренды по-прежнему из pre-computed.

### Поток пересчёта агрегатов

```
Processing Gateway:
  → INSERT review_llm_results (все результаты LLM сохранены)
  → publish AnalysisCompletedEvent { analysis_job_id, company_id, period_from, period_to }

Analytics-модуль (Web API MassTransit consumer):
  → consume AnalysisCompletedEvent { jobId, companyId }
  → SELECT reviews JOIN review_llm_results WHERE analysis_job_id = ? [из processing_db, cross-service read]
  → SELECT recommendation FROM analysis_jobs WHERE id = ? [из processing_db, cross-service read]

  → вычислить агрегат за весь период:
     INSERT analysis_snapshots (включая recommendation), branch_snapshots, topic_stats

  → вычислить недельные точки (DATE_TRUNC('week', review_date)):
     UPSERT metric_timeseries для каждой недели в периоде
     (per company × week × source)

  → publish AggregatesReadyEvent { analysis_job_id }
```

### Примеры запросов дашборда

```sql
-- KPI-карточки за конкретный анализ
SELECT nps, review_count, avg_stars, sentiment_dist, fake_ratio
FROM analysis_snapshots
WHERE analysis_job_id = ?;

-- Динамика NPS по неделям за последние 3 месяца
SELECT period_week, nps, review_count
FROM metric_timeseries
WHERE company_id = ? AND source IS NULL
  AND period_week >= CURRENT_DATE - INTERVAL '3 months'
ORDER BY period_week;

-- То же, но только по источнику '2gis'
SELECT period_week, nps, review_count
FROM metric_timeseries
WHERE company_id = ? AND source = '2gis'
  AND period_week >= CURRENT_DATE - INTERVAL '3 months'
ORDER BY period_week;

-- "Что было месяц назад?" — неделя с 2024-02-05
SELECT nps, review_count, sentiment_dist
FROM metric_timeseries
WHERE company_id = ? AND source IS NULL AND period_week = '2024-02-05';

-- Топ-темы за анализ (блок "Болевые точки")
SELECT topic, review_count, sentiment_dist
FROM topic_stats
WHERE analysis_job_id = ?
ORDER BY review_count DESC;

-- Динамика по месяцам (агрегируем недельные строки → месяц)
-- Работает и после разового анализа, и после серии мониторинговых циклов
SELECT
    DATE_TRUNC('month', period_week)  AS month,
    SUM(review_count)                 AS total,
    SUM((sentiment_dist->>'positive')::int
      + (sentiment_dist->>'very_positive')::int)  AS promoters,
    SUM((sentiment_dist->>'negative')::int
      + (sentiment_dist->>'very_negative')::int)  AS detractors
FROM metric_timeseries
WHERE company_id = ? AND source IS NULL
GROUP BY month
ORDER BY month;
-- NPS = (promoters - detractors) / total * 100 — в application layer
```

**Почему `sentiment_dist` хранит counts, а не проценты:**
`AVG(ratio)` по неделям некорректен при разном числе отзывов (неделя с 3 отзывами
имеет тот же вес, что и неделя с 200). `SUM(counts)` всегда корректен —
это единственный способ получить правильный NPS/распределение при агрегации
к любой гранулярности (месяц, квартал, год).

**Почему UPSERT для `metric_timeseries`, а не INSERT:**
Одна и та же неделя может попасть в несколько analysis_job (например, разовый анализ
и последующий live-мониторинг перекрываются). UPSERT гарантирует, что для
каждой (company, week, source) всегда актуальные данные — с учётом всех отзывов,
добавленных к этому моменту.

### Физическое размещение

По решению ADR-011 Analytics-модуль включён в Web API процесс. Его таблицы
(`analysis_snapshots`, `metric_timeseries`, `branch_snapshots`, `topic_stats`)
хранятся в **`webapi_db`** — PostgreSQL-инстансе Web API.

**Cross-service read при пересчёте агрегатов (MVP trade-off):**

> ⚠️ Analytics-модуль читает `reviews`, `review_llm_results` и `analysis_jobs` напрямую
> из `processing_db` — **schema coupling**. Принято сознательно для MVP:
> таблицы стабильны (ADR-002, ADR-004), чтение разовое (не в рантайме дашборда),
> доступ ограничен `analytics_reader` (SELECT only, только три таблицы).
> Путь устранения при экстракции модуля — в ADR-011.

```csharp
// ProcessingReadContext — read-only, только для Analytics-модуля
// MVP trade-off: schema coupling с processing_db. Заменить при экстракции Analytics.
public class ProcessingReadContext : DbContext
{
    public DbSet<Review>          Reviews      { get; set; }
    public DbSet<ReviewLlmResult> LlmResults   { get; set; }
    public DbSet<AnalysisJob>     AnalysisJobs { get; set; }

    public override int SaveChanges() =>
        throw new InvalidOperationException("Read-only context");
    public override Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Read-only context");
}
```

`analytics_reader` — отдельный PostgreSQL-пользователь с правами `SELECT`
только на `reviews`, `review_llm_results` и `analysis_jobs`. Без прав на INSERT / UPDATE / DELETE
и без доступа к другим таблицам `processing_db`.

## Consequences

**Плюсы:**
- Dashboard читает готовые числа — никаких тяжёлых запросов в рантайме
- Масштабируется с ростом пользователей: новые читатели не создают нагрузку на сырые данные
- `metric_timeseries` покрывает все сценарии динамики: неделя, месяц, квартал, «что было N назад»
- `topic_stats` per job отвечает на «топ-темы за этот период» без GROUP BY в рантайме
- Трендовые графики — trivial SELECT без GROUP BY
- Стандартный стек (EF Core + Npgsql), нет новых зависимостей
- Один PostgreSQL-инстанс в MVP: ноль дополнительных ops-расходов
- Логическое разделение позволяет вынести в отдельный инстанс без изменения кода

**Минусы / риски:**
- **MVP trade-off:** Analytics-модуль читает напрямую из `processing_db` →
  schema coupling. Риск: изменение схемы `reviews` / `review_llm_results` в PG
  требует синхронного обновления `ProcessingReadContext`. Ограничен тем, что схема
  стабильна (ADR-002) и чтение разовое. Путь ликвидации — ADR-011
- При росте числа топиков или сложности фильтров таблица `topic_stats` может вырасти
  → стандартное партиционирование по `analysis_job_id`
- Pre-computed агрегаты — «точка зрения на прошлое»: если нужна фильтрация,
  не предусмотренная при пересчёте, придётся добавлять новую dimension

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Нужна ли месячная/квартальная гранулярность в `metric_timeseries` или достаточно недельной (агрегируется на клиенте) | При проектировании дашборда |
| Нужны ли per-branch trend snapshots в `metric_timeseries` или достаточно company-level | При проектировании дашборда |
| ~~Cross-service read: API от PG или прямой доступ к таблицам при пересчёте~~ | ✅ Решено: прямой `ProcessingReadContext` (read-only DbContext) с отдельным PG-пользователем `analytics_reader` (SELECT only) |
| Динамика по темам во времени (сейчас topic_stats per job) — понадобится ли time-series по темам | После MVP, по запросу пользователей |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| TimescaleDB | Оптимизирован для time-series по сырым данным; у нас pre-computed снапшоты — трендовые запросы тривиальны на plain PG |
| ClickHouse | OLAP для raw-агрегации на миллиардах строк; несовместимо с паттерном pre-computed; ops-оверхед без выигрыша |
| Analytics-модуль читает raw данные на каждый запрос дашборда | При росте числа пользователей и компаний создаёт предсказуемую нагрузку; каждый просмотр дашборда — тяжёлый GROUP BY |
| Отдельный PostgreSQL-инстанс с нуля | Преждевременно при MVP; логическое разделение уже достаточно |
