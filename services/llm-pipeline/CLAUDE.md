# Обратка — LLM-пайплайн анализа отзывов

## Контекст для Claude

Это система анализа отзывов с площадок (Яндекс.Карты, 2GIS, Google Maps и т.п.) через LLM-пайплайн. На вход — сырые отзывы, на выходе — KPI, болевые точки и рекомендации для бизнеса.

**Целевые объёмы:** от 500 до 15 000 отзывов за цикл анализа.
**Бюджет на цикл:** ~$0.07 (малый) → ~$1.75 (крупный).

## Архитектурные решения (зафиксированы)

- **LLM-роутер:** OpenRouter (единая точка входа для всех моделей)
- **JSON-валидация:** `instructor` + Pydantic (auto-retry на невалидных ответах)
- **Логирование:** `loguru` (см. `tasks/00_logging.md`)
- **Параллелизация:** `asyncio` + `aiohttp` (см. `tasks/01_async_orchestrator.md`)
- **Хранилище:** PostgreSQL (отзывы, результаты, кэш по хэшу текста) — будущая задача; текущая реализация `tasks/12` БД не трогает
- **Очередь low-confidence:** отдельный буфер для повторного прогона через сильную модель (см. `tasks/05_step2_topics_sentiment.md`)

## Поток данных

```
Сырые отзывы
    ↓
[Шаг 0]   Нормализация (algo)
    ↓
[Шаг 0.5] Перевод не-RU → Gemini 2.0 Flash    [опционально]
    ↓
[Шаг 1]   Детекция фейков → GPT-4o-mini       [по авторам, будущая реализация]
    ↓ (отфильтрованы фейки)
[Шаг 2]   Темы + тональность → GPT-4o-mini    [batch 10–15]
    ├──→ confidence ≥ 0.5 → результат
    └──→ confidence < 0.5 → очередь low-confidence
              ↓
[Шаг 2.2] Переклассификация → GPT-4o          [сильная модель]
    ↓
[Шаг 2.1] Кластеризация свободных тем → GPT-4o-mini
    ↓
[Шаг 3]   Агрегация KPI (algo)
    ↓
[Шаг 4]   Рекомендации → DeepSeek V3.2
```

## Структура задач

Все задачи лежат в `tasks/` и пронумерованы в порядке реализации:

| Файл | Что делает |
|------|-----------|
| `00_logging.md` | Настройка loguru, форматы, ротация |
| `01_async_orchestrator.md` | Асинхронный раннер батчей, rate limiting, ретраи |
| `02_step0_normalization.md` | Нормализация текста, langdetect |
| `03_step05_translation.md` | Перевод не-RU отзывов через Gemini 2.0 Flash |
| `04_step1_fake_detection.md` | Детекция фейков по профилям авторов |
| `05_step2_topics_sentiment.md` | Темы + тональность + **очередь low-confidence** |
| `06_step22_reclassification.md` | Прогон low-confidence через GPT-4o |
| `07_step21_topic_clustering.md` | Кластеризация свободных тем |
| `08_step3_kpi_aggregation.md` | Алгоритмическая агрегация KPI |
| `09_step4_recommendations.md` | Генерация рекомендаций через DeepSeek |
| `10_database_schema.md` | Схема БД, кэш по хэшу текста |
| `11_config_and_env.md` | `.env`, конфиг моделей, цены |

## Структура кода

```
obratka/
├── pyproject.toml
├── .env.example
├── CLAUDE.md
├── tasks/                    # ТЗ для каждого модуля
├── docs/
│   └── pricing.md            # расчёт стоимости
├── src/
│   └── obratka/
│       ├── __init__.py
│       ├── analyze_reviews.py # основной контрактный entrypoint: 2 JSON на выходе
│       ├── config.py         # настройки, цены моделей
│       ├── logging_setup.py  # loguru конфиг
│       ├── orchestrator.py   # legacy/dev раннер с единым JSON и HTML-отчётом
│       ├── llm/
│       │   ├── __init__.py
│       │   ├── client.py     # OpenRouter клиент (async)
│       │   ├── retry.py      # ретраи + rate limit
│       │   └── schemas.py    # Pydantic-схемы для каждого шага
│       ├── steps/
│       │   ├── __init__.py
│       │   ├── step0_normalize.py
│       │   ├── step05_translate.py
│       │   ├── step1_fake_detect.py
│       │   ├── step2_topics.py
│       │   ├── step22_reclassify.py
│       │   ├── step21_cluster.py
│       │   ├── step3_kpi.py
│       │   └── step4_recommend.py
│       ├── db/
│       │   ├── __init__.py
│       │   ├── models.py     # SQLAlchemy / Pydantic
│       │   └── cache.py      # кэш по хэшу
│       └── utils/
│           ├── lang.py       # langdetect обёртка
│           └── hashing.py    # стабильный хэш текста
└── tests/
    └── ...
```

## Принципы реализации

1. **Каждый LLM-шаг возвращает строго валидный JSON** через `instructor` + Pydantic. Невалидный ответ → автоматический retry с подсказкой об ошибке.
2. **Кэширование на уровне отзыва.** Хэш нормализованного текста + версия промпта → результат. Повторная обработка только при изменении промпта.
3. **Все LLM-вызовы идут через единый async-клиент** (`llm/client.py`) с rate limiting и экспоненциальным backoff.
4. **Логирование на каждом шаге:** вход, выход, latency, стоимость в токенах, ошибки. Один `request_id` пробрасывается через весь пайплайн.
5. **Параллелизм по батчам.** Шаги 1, 2, 2.2 пускаются через `asyncio.gather` с семафором (ограничение конкурентности).

## Что НЕ делать

- Не хардкодить цены — они в `config.py`, читаются из `.env`/конфига.
- Не вызывать модели напрямую через openai/google sdk — всё через OpenRouter.
- Не блокировать event loop синхронными HTTP-вызовами.
- Не игнорировать low-confidence результаты — они идут во вторичную очередь, см. `tasks/05` и `tasks/06`.

## Команда запуска

```bash
poetry run python -m obratka.analyze_reviews --input input.json --out-dir ./out
```

## Старт разработки

Реализовывать задачи в порядке нумерации. Каждый `.md` в `tasks/` — самодостаточное ТЗ с примерами входа/выхода и критериями готовности.
