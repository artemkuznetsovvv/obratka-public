# Задача 04: Шаг 1 — Детекция фейков и накруток

## Цель

Найти авторов с подозрительными паттернами публикаций и пометить их отзывы как `suspicious=True`. Эти отзывы исключаются из основного анализа (Шаги 2+) — экономим токены.

> Статус v2: шаг 1 пока не реализован в коде и не входит в текущую задачу
> согласования `tasks/12`. Схемы артефактов под `Step1Artifact` оставлены для
> будущей реализации.

## Файлы

- `src/obratka/steps/step1_fake_detect.py`

## Модель

- **GPT-4o-mini** через OpenRouter
- Цены: вход $0.15 / выход $0.60 за 1M токенов
- OpenRouter ID: `openai/gpt-4o-mini`

## Гранулярность

Шаг работает **на уровне автора**, а не отдельного отзыва. На вход — все известные отзывы одного автора (то, что удалось спарсить с площадки). На выход — один вердикт на автора, который применяется ко всем его отзывам в текущем датасете.

## Pydantic-схемы

```python
class AuthorReviews(BaseModel):
    author_id: str
    reviews: list[AuthorReviewItem]   # все отзывы этого автора с площадки

class AuthorReviewItem(BaseModel):
    text: str          # text_normalized или text_translated
    stars: int | None
    date: datetime

class FakeDetectionOutput(BaseModel):
    """JSON-ответ от LLM."""
    suspicious: bool
    confidence: float = Field(ge=0.0, le=1.0)
    reason: str

class AuthorVerdict(BaseModel):
    author_id: str
    suspicious: bool | None        # None — если профиль автора недоступен
    confidence: float | None
    reason: str | None
```

## Системный промпт

```
Ты — система детекции фейков и накруток отзывов.
Проанализируй историю отзывов автора и определи, является ли он накрутчиком.

Признаки фейка:
1. Все отзывы автора однополярные (только 5★ или только 1★).
2. Аномально высокая частота публикаций (10+ отзывов за один день).
3. Одинаковые или почти идентичные формулировки в разных отзывах.
4. Отсутствие конкретики — только эмоции без деталей.
5. Шаблонные фразы, характерные для заказных отзывов.

Если данных мало (1–2 отзыва) — confidence не выше 0.3.

Верни строго JSON:
{
  "suspicious": true/false,
  "confidence": 0.0–1.0,
  "reason": "краткое объяснение на русском, 1 предложение"
}
```

User-promt — JSON со списком отзывов автора (см. пример в исходном ТЗ).

## Логика

1. Группируем нормализованные отзывы по `author_id`.
2. Если `author_id is None` (площадка не отдала) → `suspicious=None`, пропускаем шаг для этого отзыва.
3. Для каждого автора, у которого ≥1 отзыва, делаем LLM-вызов через `instructor`.
4. Применяем вердикт ко всем отзывам автора в текущем датасете.
5. Отзывы с `suspicious=True` исключаются из дальнейшей обработки, но сохраняются в БД для статистики (доля фейков идёт в KPI на Шаге 3).

## Параллелизация

- Авторы обрабатываются через `BatchRunner.run_many`, конкурентность 8–12.
- Один автор = один LLM-вызов независимо от количества отзывов у него.

## API

```python
async def detect_fake_author(
    author: AuthorReviews,
    llm: LLMClient,
) -> AuthorVerdict: ...

async def step1_detect_fakes(
    reviews: list[NormalizedReview],
    runner: BatchRunner,
    llm: LLMClient,
) -> tuple[list[NormalizedReview], dict[str, AuthorVerdict]]:
    """Возвращает (отзывы без фейков, словарь вердиктов по авторам)."""
```

## Логирование

- INFO старт: `"Fake detection start"`, `authors_count=...`.
- DEBUG на каждого автора: `author_id`, `suspicious`, `confidence`, `reason`, `cost_usd`, `latency_ms`.
- INFO итог: `"Fake detection done"`, `suspicious_authors=N`, `excluded_reviews=M`, `cost_usd=...`.

## Критерии готовности

- [ ] Юнит-тест на мок-LLM: вход — 3 одинаковых 5★ отзыва за один день → `suspicious=True`.
- [ ] Юнит-тест на мок-LLM: вход — 5 разнообразных отзывов за месяц → `suspicious=False`.
- [ ] При невалидном JSON после ретраев — `suspicious=None`, лог ERROR, пайплайн не падает.
- [ ] Кэш по `author_id` (опционально) — если автор уже проверялся в недавнем запуске.

## Подсказки

- Если у автора больше 50 отзывов — обрезаем до последних 50, чтобы уложиться в контекст.
- Для воспроизводимости temperature=0.0.
