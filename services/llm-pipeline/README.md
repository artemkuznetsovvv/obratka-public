# Обратка — LLM-пайплайн анализа отзывов

Анализ отзывов с площадок (Яндекс.Карты, 2GIS, Google Maps) через LLM. На вход — сырые отзывы, на выходе — KPI, болевые точки и рекомендации.

## Старт

1. Открой `CLAUDE.md` — это обзорное ТЗ для Claude Code.
2. Реализуй задачи из `tasks/` по порядку — каждый файл это самодостаточное ТЗ.
3. Ключевые файлы:
   - `tasks/00_logging.md` — loguru
   - `tasks/01_async_orchestrator.md` — asyncio + aiohttp параллелизация
   - `tasks/05_step2_topics_sentiment.md` — Шаг 2 с low-confidence очередью
   - `tasks/06_step22_reclassification.md` — повторный прогон через GPT-4o

## Стек

- Python 3.11+
- `asyncio` + `aiohttp` — параллелизм
- `instructor` + Pydantic v2 — структурированные ответы LLM
- `loguru` — логирование
- `OpenRouter` — единый API для всех моделей
- PostgreSQL + SQLAlchemy 2.0 (async) — запланированное хранилище и кэш, в текущей реализации не подключены

## Команды

```bash
# Установка
poetry install

# Контрактный LLM-анализ отзывов: создаёт ровно два JSON-файла
poetry run python -m obratka.analyze_reviews --input input.json --out-dir ./out

# Быстрая локальная проверка контракта без LLM
poetry run python -m obratka.analyze_reviews --input input.json --out-dir ./out --local

# Старый dev-оркестратор с HTML-отчётом
poetry run python -m obratka.orchestrator --input reviews.json --business-id 42

# Тесты
poetry run pytest
```

`obratka.analyze_reviews` — основной production entrypoint. По умолчанию он
запускает LLM-пайплайн шагов 0/0.5/2/2.1/2.2/3/4 и ожидает входной JSON формата:
`schema_version`, `analysis_job_id`, `company_id`, `reviews[]`.
После выполнения в `--out-dir` появляются:

- `review_aspects.json` — аспектный анализ каждого отзыва;
- `recommendations.json` — агрегированные рекомендации.

По умолчанию dev-визуализация и Phoenix отключены для production-friendly
запуска. Локально их можно включить через `.env`, но generated reports/logs не
публикуются в GitHub.

## Бюджет на цикл

- 500 отзывов: ~$0.20
- 3 000 отзывов: ~$1.16
- 15 000 отзывов: ~$3.99

См. `docs/pricing.md`.
