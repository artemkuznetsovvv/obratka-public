# Задача 09: Шаг 4 — Генерация рекомендаций (DeepSeek V3.2)

## Цель

Сгенерировать связный, осмысленный текст с рекомендациями для бизнеса на основе KPI и болевых точек. Один LLM-вызов на весь анализ.

> v2 / tasks/12: в prompt передаются raw, weighted и fresh KPI/loyalty.
> Для приоритизации используется weighted-срез, а fresh-срез помогает находить
> свежий негатив или улучшения. Функция принимает `collector` и пишет
> `Step4Artifact`.
> Рекомендации строятся только на основе часто повторяющихся недостатков:
> минимум 3 негативных упоминания или `weighted_negative_mention_count >= 2.0`
> при `mention_count >= 5`. Единичные жалобы не становятся рекомендациями.

## Файлы

- `src/obratka/steps/step4_recommend.py`

## Модель

- **DeepSeek V3.2** через OpenRouter
- Цены: вход $0.26 / выход $0.38 за 1M токенов
- OpenRouter ID: `deepseek/deepseek-chat`

Альтернатива для премиум-тарифа: `anthropic/claude-sonnet-4-5` — лучше по русскому языку, но в ~10× дороже.

## Pydantic-схемы

```python
RecType = Literal["strategic", "tactical", "communication"]

class Recommendation(BaseModel):
    type: RecType
    priority: int = Field(ge=1, le=5)        # 1 = высший приоритет
    topic: str | None                        # к какой теме относится
    title: str                               # короткий заголовок
    body: str                                # развёрнутая рекомендация
    expected_impact: str                     # качественный/операционный эффект, без процентов
    evidence: list[str]                      # цитаты из отзывов / KPI

class RecommendationsOutput(BaseModel):
    summary: str                             # короткое резюме ситуации
    recommendations: list[Recommendation]
```

## Системный промпт

```
Ты — консультант по управлению репутацией бизнеса.
Тебе дан анализ отзывов и KPI клиента. Сгенерируй рекомендации трёх типов:

1. strategic — долгосрочные изменения процессов / продукта (3–5 шт).
2. tactical — конкретные действия на ближайший месяц (3–5 шт).
3. communication — что и как ответить клиентам, как работать с негативом (2–3 шт).

Для каждой рекомендации:
- Привяжи к данным (цитата отзыва или KPI).
- Укажи expected_impact в качественных или операционных терминах.
- Никогда не используй проценты, знак % или точные числовые прогнозы в expected_impact.
  Запрещены формулировки вроде «снижение негатива на 20%». Используй формулировки
  вроде «меньше повторяющихся жалоб на парковку» или «выше предсказуемость сервиса».
- Отсортируй по priority (1 — критично, 5 — nice-to-have).

Если NPS-индекс ниже 0 — фокус на устранение негатива.
Если есть тема с резким ростом негатива (>50%) — это всегда priority 1 strategic.

Верни строго JSON по схеме.
```

В user-prompt передаём:
- Контекст бизнеса: тип, локация, число точек, период.
- Core KPI.
- Loyalty Index.
- Top-5 болевых точек со sample_fragments.
- Rare pain points передаются отдельно как ignored/weak signals и не должны
  становиться основой рекомендаций.
- Top позитивных черт со sample_fragments.
- Динамика за период (week/month/quarter).
- Fake stats.

## API

```python
async def generate_recommendations(
    pipeline_result: PipelineResult,   # без поля recommendations
    business_context: BusinessContext,
    llm: LLMClient,
) -> RecommendationsOutput: ...
```

```python
class BusinessContext(BaseModel):
    business_id: int
    name: str | None
    business_type: str           # "ресторан" | "клиника" | ...
    location: str | None
    branches_count: int = 1
    custom_notes: str | None     # дополнительный контекст от пользователя
```

## Версионирование промпта

Версия промпта **обязательно** фиксируется в БД для воспроизводимости и сравнения качества.

```python
PROMPT_VERSION = "step4-recommendations-v1.0"
```

При генерации в результат пишется `prompt_version`, чтобы при изменении промпта можно было перепрогнать только новые версии.

## Стоимость

~15К токенов на вызов (входные KPI + few-shot + сам отчёт)
≈ **$0.005–0.01** на анализ.

## Логирование

- INFO старт: `business_id`, `pain_points_count`, `nps_score`.
- DEBUG: полный prompt (только в DEBUG, может быть длинным).
- INFO итог: `recommendations_count`, `cost_usd`, `latency_ms`, `prompt_version`.

## Критерии готовности

- [ ] При невалидном JSON instructor ретраит до 3 раз.
- [ ] Если у клиента всё хорошо (negative_share < 10%, NPS > 50) — рекомендации сгенерируются, но fokus сместится на «как удержать качество».
- [ ] Юнит-тест на мок-LLM: при `negative_share=0.5` рекомендации содержат хотя бы одну strategic с priority=1.
- [ ] Версия промпта пишется в БД вместе с результатом.

## Подсказки

- Для DeepSeek через OpenRouter — тот же openai SDK + base_url=`https://openrouter.ai/api/v1`.
- `temperature=0.3` (не 0, чтобы рекомендации не были однообразными между запусками, но и не слишком креативными).
- Можно дать модели one-shot пример правильного ответа в промпте — повышает качество.
