# ADR-002: База данных для сырых отзывов и результатов LLM

## Context

Системе нужно хранить:
1. **Сырые отзывы** — данные, собранные парсером (текст, дата, источник, звёзды, филиал)
2. **Результаты LLM per review** — тональность, темы, фейк-статус, confidence,
   полученные от внешнего LLM-сервиса через Processing Gateway

**Характеристики данных:**
- Схема отзыва стабильна и хорошо определена
- Ключевые требования к хранилищу:
  - Дедупликация при вставке (UNIQUE constraint на `external_id` / `composite_key`)
  - Фильтрация для дашборда: по дате, источнику, филиалу, тональности, теме, звёздам
  - Связь отзыв ↔ результат LLM (1:1 per analysis job)
- Объём MVP: до 100 000 отзывов суммарно (1000/компания × 100 компаний)
- Хранение: бессрочно
- Стек: .NET 8, EF Core

**Что НЕ хранится здесь:**
Агрегированные метрики и временны́е ряды для дашборда (NPS, динамика, болевые точки)
— это ответственность Analytics-модуля (Web API), решается в ADR-003.

## Decision

### PostgreSQL

Единственная БД для сырых отзывов и результатов LLM.

**Почему PostgreSQL:**
- Схема стабильна → реляционная модель оптимальна, ACID для дедупликации
- `UNIQUE` constraint на `composite_key` гарантирует отсутствие дублей на уровне БД
- `JSONB` для массивов переменной длины (topics, fake_reason_tags) + GIN-индекс
  для фильтрации по теме на дашборде
- 100 000 строк — тривиальный объём для PostgreSQL
- EF Core + Npgsql — зрелая интеграция, стандарт для .NET
- MongoDB не нужен: схема не меняется, гибкость документов не даёт преимуществ

### Схема таблиц

```sql
-- Сырые отзывы (Processing Gateway пишет после скачивания из S3)
CREATE TABLE reviews (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL,
    branch_id       UUID NOT NULL,
    source          VARCHAR(50) NOT NULL,          -- '2gis', 'yandex', 'google', 'otzovik'
    external_id     VARCHAR(500),                  -- уникальный ID от источника (nullable)
    composite_key   VARCHAR(1000) NOT NULL UNIQUE, -- source+branch+date+normalized_text
    raw_text        TEXT NOT NULL,
    normalized_text TEXT,                          -- после нормализации (заполняет LLM)
    review_date     TIMESTAMPTZ NOT NULL,
    stars           SMALLINT,                      -- nullable: не все источники дают оценку
    collected_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX idx_reviews_external
    ON reviews (source, branch_id, external_id)
    WHERE external_id IS NOT NULL;

CREATE INDEX idx_reviews_company_date
    ON reviews (company_id, review_date DESC);

-- Результаты LLM per review (Processing Gateway пишет)
CREATE TABLE review_llm_results (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    review_id            UUID NOT NULL REFERENCES reviews(id),
    analysis_job_id      UUID NOT NULL,
    fake_status          VARCHAR(20) NOT NULL,      -- 'normal', 'suspicious', 'fake'
    fake_reason_tags     JSONB NOT NULL DEFAULT '[]', -- ["однотипный текст", ...]
    sentiment            VARCHAR(20),               -- 'very_negative'..'very_positive', nullable при неуверенности
    sentiment_confidence FLOAT,
    is_spam              BOOLEAN NOT NULL,
    spam_confidence      FLOAT NOT NULL,
    topics               JSONB NOT NULL DEFAULT '[]', -- ["персонал", "еда"]
    -- recommendation НЕ хранится per review: LLM возвращает recommendation как строку на уровне job
    -- Processing Gateway сохраняет её в analysis_jobs.recommendation (ADR-004)
    processed_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    UNIQUE (review_id, analysis_job_id)             -- один результат per review per job
);

CREATE INDEX idx_llm_results_job
    ON review_llm_results (analysis_job_id);

-- GIN-индекс для фильтрации по теме ("где тема = 'персонал'")
CREATE INDEX idx_llm_results_topics_gin
    ON review_llm_results USING GIN (topics);
```

### Владение данными по сервисам

| Таблица | Пишет | Читает |
|---------|-------|--------|
| `reviews` | Processing Gateway | Analytics-модуль (Web API), Web API |
| `review_llm_results` | Processing Gateway | Analytics-модуль (Web API), Web API |

**Важно:** Parser Service — stateless, он не хранит отзывы. Результат парсинга
Parser записывает в S3 (`s3://obratka-jobs/{job_id}/raw/{source}.json`),
Processing Gateway скачивает из S3 и сохраняет в таблицу `reviews` (ADR-001).

Таблицы `reviews` и `review_llm_results` хранятся в `processing_db` —
отдельном PostgreSQL-инстансе Processing Gateway (ADR-011).
Analytics-модуль (внутри Web API) читает из `processing_db` только при пересчёте агрегатов —
один раз за цикл, не в рантайме дашборда.

Processing Gateway в MVP использует **один PostgreSQL-инстанс** для обеих таблиц.
При необходимости разделить схему позже — владение уже чётко разграничено.

### Дедупликация на уровне БД

```sql
-- Вставка с игнорированием дубля (Parser Service использует)
INSERT INTO reviews (...) VALUES (...)
ON CONFLICT (composite_key) DO NOTHING;
```

Приоритет ключей (из ADR-004):
1. `external_id` (если источник даёт) → UNIQUE INDEX на `(source, branch_id, external_id)`
2. `composite_key` → UNIQUE constraint (fallback)

## Consequences

**Плюсы:**
- Стандартный стек: EF Core + Npgsql, никаких дополнительных зависимостей
- Дедупликация на уровне БД — надёжно при конкурентных вставках
- JSONB + GIN для массивов: гибко и быстро для фильтрации по темам
- Один инстанс для Parser + Processing Gateway в MVP — просто в эксплуатации

**Минусы / риски:**
- При росте нагрузки (миллионы отзывов) потребуется партиционирование по `company_id`
  или `review_date` — стандартная операция в PostgreSQL
- JSONB для topics не даёт foreign key на таблицу тем → нужен application-level контроль
  допустимых значений (или отдельная таблица `review_topics` при нормализации)

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Нормализовать topics в отдельную таблицу или оставить JSONB | При реализации Analytics-модуля (зависит от сложности агрегаций) |
| Нужна ли soft-delete для отзывов (пометка `is_deleted`) | При проектировании API |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| MongoDB | Схема стабильна → гибкость документов не нужна; EF Core для Mongo менее зрелый; усложняет стек |
| Отдельные БД для Parser и Processing Gateway | Преждевременное усложнение при MVP-масштабе; данные тесно связаны (review ↔ llm_result) |
| PostgreSQL + TimescaleDB | TimescaleDB оптимален для time-series агрегатов — это задача ADR-003, не ADR-002 |
