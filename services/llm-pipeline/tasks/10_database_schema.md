# Задача 10: Схема БД и кэширование

## Цель

Хранить отзывы, результаты каждого шага и кэш по хэшу текста. Кэш экономит 90–95% LLM-вызовов при ежедневном мониторинге.

## Файлы

- `src/obratka/db/models.py`
- `src/obratka/db/cache.py`

## Стек

```toml
sqlalchemy = "^2.0"
asyncpg = "^0.29"           # async PostgreSQL драйвер
alembic = "^1.13"           # миграции
```

## Схема

### `reviews` — сырые и нормализованные отзывы

| Поле | Тип | Назначение |
|---|---|---|
| `id` | UUID PK | |
| `external_id` | TEXT | ID на площадке |
| `source` | TEXT | "yandex" / "2gis" / "google" |
| `business_id` | INT FK | |
| `author_id` | TEXT NULL | |
| `text_raw` | TEXT | |
| `text_normalized` | TEXT | |
| `text_translated` | TEXT NULL | |
| `text_hash` | TEXT INDEX | sha256 от text_normalized |
| `lang` | TEXT | |
| `stars` | INT NULL | |
| `posted_at` | TIMESTAMP | |
| `collected_at` | TIMESTAMP | |
| `is_suspicious` | BOOL NULL | |

UNIQUE: `(source, external_id)`.

### `analyses` — результаты Шагов 2 и 2.2

| Поле | Тип |
|---|---|
| `id` | UUID PK |
| `review_id` | UUID FK |
| `text_hash` | TEXT INDEX |
| `prompt_version` | TEXT |
| `model` | TEXT | `gpt-4o-mini` или `gpt-4o` |
| `is_mixed` | BOOL |
| `overall_sentiment` | TEXT |
| `overall_confidence` | FLOAT |
| `low_confidence_final` | BOOL |
| `aspects_json` | JSONB | список `Aspect` |
| `created_at` | TIMESTAMP |

INDEX: `(text_hash, prompt_version, model)` — для кэша.

### `author_verdicts` — результаты Шага 1

| Поле | Тип |
|---|---|
| `author_id` | TEXT PK |
| `business_id` | INT FK |
| `suspicious` | BOOL NULL |
| `confidence` | FLOAT NULL |
| `reason` | TEXT |
| `prompt_version` | TEXT |
| `checked_at` | TIMESTAMP |

### `translations` — кэш переводов (Шаг 0.5)

| Поле | Тип |
|---|---|
| `text_hash` | TEXT PK |
| `source_lang` | TEXT |
| `text_ru` | TEXT |
| `prompt_version` | TEXT |
| `created_at` | TIMESTAMP |

### `pipeline_runs` — записи о запусках

| Поле | Тип |
|---|---|
| `run_id` | UUID PK |
| `business_id` | INT |
| `started_at` | TIMESTAMP |
| `finished_at` | TIMESTAMP NULL |
| `status` | TEXT | "running" / "done" / "failed" |
| `total_reviews` | INT |
| `total_cost_usd` | FLOAT |
| `result_json` | JSONB | финальный `PipelineResult` |
| `error` | TEXT NULL |

### `topic_clusters` — карты кластеризации (Шаг 2.1)

| Поле | Тип |
|---|---|
| `id` | UUID PK |
| `business_id` | INT |
| `mapping_json` | JSONB |
| `created_at` | TIMESTAMP |

## API кэша

```python
class AnalysisCache:
    async def get(
        self, text_hash: str, prompt_version: str, model: str
    ) -> ReviewAnalysis | None: ...

    async def set(
        self, text_hash: str, prompt_version: str, model: str,
        analysis: ReviewAnalysis,
    ) -> None: ...

class TranslationCache:
    async def get(self, text_hash: str, prompt_version: str) -> str | None: ...
    async def set(self, text_hash: str, prompt_version: str, text_ru: str) -> None: ...

class AuthorCache:
    async def get(
        self, author_id: str, prompt_version: str, ttl_hours: int = 24,
    ) -> AuthorVerdict | None: ...
```

## Логика кэш-хитов

В каждом шаге **до** LLM-вызова:

```python
cached = await cache.get(text_hash, PROMPT_VERSION, MODEL)
if cached:
    log.bind(review_id=review_id).debug("Cache hit", text_hash=text_hash[:8])
    return cached
result = await llm.complete(...)
await cache.set(text_hash, PROMPT_VERSION, MODEL, result)
```

При смене `PROMPT_VERSION` — кэш автоматически инвалидируется (новый ключ).

## Критерии готовности

- [ ] Миграции через alembic создают все таблицы.
- [ ] Юнит-тесты с testcontainers PostgreSQL: записать → прочитать → проверить.
- [ ] При повторном запуске пайплайна на тех же отзывах — 95%+ кэш-хитов в `analyses`.
- [ ] Кэш `author_verdicts` инвалидируется через `ttl_hours` (новые отзывы у автора могут изменить вердикт).
- [ ] `pipeline_runs.result_json` содержит полный `PipelineResult` для воспроизведения отчёта без перепрогона.

## Подсказки

- Используй `sqlalchemy.ext.asyncio` + `AsyncSession`.
- JSONB поля — для гибкости. Не нормализуем `aspects` в отдельные таблицы — это лишняя сложность.
- Индекс `(text_hash, prompt_version, model)` — обязательно, иначе кэш будет тормозить на больших объёмах.
