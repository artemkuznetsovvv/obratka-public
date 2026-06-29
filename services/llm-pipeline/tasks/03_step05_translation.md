# Задача 03: Шаг 0.5 — Перевод не-RU отзывов

## Цель

Перевести на русский отзывы, у которых `lang != "ru"` (после Шага 0). Используем самую дешёвую достаточно качественную модель — Gemini 2.0 Flash через OpenRouter.

> v2 / tasks/12: `step05_translate_all` оборачивается в `step_span("step05")`,
> принимает `collector: ArtifactCollector | None` и пишет `Step05Artifact`.
> Кэш переводов через БД не реализуется в текущей задаче.

## Файлы

- `src/obratka/steps/step05_translate.py`

## Модель

- **Gemini 2.0 Flash** через OpenRouter
- Цены: вход $0.10 / выход $0.40 за 1M токенов
- OpenRouter ID: `google/gemini-2.0-flash-001`

## Триггер

Шаг вызывается **только** для отзывов с `lang != "ru"` и `lang_confidence >= 0.6`. Для `unknown` — пропускаем (не переводим, идёт в шаг 1 как есть).

## Pydantic-схемы

```python
class TranslatedReview(BaseModel):
    review_id: str
    text_translated: str
    source_lang: str

class TranslationOutput(BaseModel):
    """Структурированный выход от LLM для одного отзыва."""
    text_ru: str = Field(..., description="Перевод на русский язык")
```

## Системный промпт

```
Ты — переводчик коротких отзывов о бизнесах (рестораны, магазины, услуги).
Переводи текст на русский язык, сохраняя:
— тональность (позитив/негатив/нейтрал),
— сленг и эмоциональную окраску,
— конкретные детали (имена сотрудников, блюд, услуг).

Не добавляй ничего от себя. Не комментируй перевод.
Верни строго JSON: {"text_ru": "..."}.
```

## Пример

**Вход (`text_normalized`):**
```
the food was great but the waiter was extremely rude
```

**Выход (`text_translated`):**
```
еда была отличная, но официант был очень грубым
```

## Параллелизация

- Каждый перевод — отдельный LLM-вызов (короткие тексты, батчить смысла мало).
- Все вызовы запускаются через `BatchRunner.run_many` с конкурентностью 8–12.
- Кэш: если для `text_hash + версия_промпта` уже есть перевод в БД — пропускаем.

## API

```python
async def translate_review(
    review: NormalizedReview,
    llm: LLMClient,
    cache: TranslationCache,
) -> TranslatedReview: ...

async def step05_translate_all(
    reviews: list[NormalizedReview],
    runner: BatchRunner,
    llm: LLMClient,
    cache: TranslationCache,
) -> dict[str, TranslatedReview]:
    """Возвращает {review_id: TranslatedReview} только для тех, кого реально переводили."""
```

## Логирование

- INFO старт: `"Translation step start"`, `non_ru_count=...`.
- DEBUG на каждый перевод: `review_id`, `source_lang`, `prompt_tokens`, `completion_tokens`, `cost_usd`, `latency_ms`.
- INFO итог: `"Translation step done"`, `translated=N`, `cached=M`, `cost_usd=...`.

## Критерии готовности

- [ ] Шаг вызывается только для не-RU отзывов.
- [ ] Используется `instructor` для валидации `TranslationOutput`.
- [ ] Кэш работает по `(text_hash, prompt_version)`.
- [ ] При невалидном JSON после 3 ретраев — пишем ERROR, отзыв идёт дальше с `text_translated = text_normalized` и пометкой `translation_failed=True`.
- [ ] Замер на 100 английских отзывах: суммарная стоимость ≤ $0.005.

## Подсказки

- `instructor` для Gemini через OpenRouter работает так же, как для OpenAI — он только формирует messages и валидирует ответ.
- Для увеличения скорости можно поднять `max_concurrency` до 16 — Gemini 2.0 Flash имеет высокий RPS.
