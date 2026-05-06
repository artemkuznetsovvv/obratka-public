# Задача 05: Шаг 2 — Темы и тональность (с low-confidence очередью)

## Цель

Главный аналитический шаг: для каждого отзыва извлечь упомянутые темы (аспекты), их тональность и общую тональность отзыва. Отзывы с низкой уверенностью модели **не выбрасываются**, а собираются в отдельный батч и прогоняются через более сильную модель в Шаге 2.2.

> v2 / tasks/12: `step2_run` оборачивается в `step_span("step2")`, принимает
> `collector: ArtifactCollector | None` и пишет `Step2Artifact` с распределением
> confidence, high/low-conf сэмплами, темами и parse failures.
> После ответа LLM темы аспектов проходят детерминированную нормализацию:
> очевидные синонимы и служебные ошибки (`кофе/напитки`, `is_freeform=true`)
> приводятся к базовым темам до KPI/отчёта.

## Файлы

- `src/obratka/steps/step2_topics.py`

## Модель

- **GPT-4o-mini** через OpenRouter
- Цены: вход $0.15 / выход $0.60 за 1M токенов
- OpenRouter ID: `openai/gpt-4o-mini`

## Pydantic-схемы

```python
SENTIMENT = Literal[
    "очень негативный",
    "негативный",
    "нейтральный",
    "позитивный",
    "очень позитивный",
    "смешанный",
]

class Aspect(BaseModel):
    topic: str                              # тема: "еда", "персонал", ...
    sentiment: SENTIMENT
    confidence: float = Field(ge=0.0, le=1.0)
    fragment: str                           # цитата из отзыва, обосновывающая аспект
    is_freeform: bool = False               # тема вне базового набора?

class ReviewAnalysis(BaseModel):
    review_id: str
    is_mixed: bool
    overall_sentiment: SENTIMENT
    overall_confidence: float = Field(ge=0.0, le=1.0)
    aspects: list[Aspect]

class BatchOutput(BaseModel):
    """JSON-ответ от LLM на один батч."""
    items: list[ReviewAnalysis]
```

## Базовый набор тем

```python
BASE_TOPICS = [
    "еда/напитки",
    "персонал",
    "скорость обслуживания",
    "чистота",
    "цена/качество",
    "атмосфера",
    "локация/парковка",
    "запись/коммуникация",
]
```

LLM может выделять и другие темы (свободные), они потом кластеризуются на Шаге 2.1.

## Системный промпт

```
Ты — система анализа отзывов о бизнесах.
Для каждого отзыва из батча определи:
1. Все упомянутые темы (аспекты). Используй базовый набор тем где возможно;
   если нужна тема вне списка — добавь её и пометь is_freeform=true.
2. Тональность каждого аспекта.
3. Общую тональность отзыва (overall_sentiment).
4. is_mixed=true, если в отзыве есть и явный позитив, и явный негатив.
5. confidence для каждого аспекта и для overall_sentiment — насколько ты уверена.
   Если формулировка размыта, ирония, сарказм или мало контекста — confidence ниже.

Базовый набор тем:
- еда/напитки
- персонал
- скорость обслуживания
- чистота
- цена/качество
- атмосфера
- локация/парковка
- запись/коммуникация

Для каждого аспекта приведи fragment — точную цитату из отзыва.
Верни строго JSON по схеме {"items": [...]}.
```

## Батчирование

- **Размер батча:** 12 отзывов (диапазон 10–15, при >20 растёт % ошибок парсинга).
- Батчи формируются после фильтрации фейков (Шаг 1).
- Каждый батч — отдельный LLM-вызов, через `BatchRunner.run_many`, конкурентность 8.

## ⭐ Low-confidence очередь (ключевая логика)

После получения результатов от GPT-4o-mini для каждого отзыва считаем:

```python
LOW_CONF_THRESHOLD = 0.5    # из конфига

def needs_reclassification(analysis: ReviewAnalysis) -> tuple[bool, str]:
    # 1. Общая уверенность низкая
    if analysis.overall_confidence < LOW_CONF_THRESHOLD:
        return True, "low_overall_confidence"
    # 2. Хотя бы один аспект с низкой уверенностью
    if any(a.confidence < LOW_CONF_THRESHOLD for a in analysis.aspects):
        return True, "low_aspect_confidence"
    return False, ""
```

**Что происходит с такими отзывами:**

1. Они **не теряются** и **не помечаются финально**.
2. Они складываются в отдельный буфер `low_conf_queue: list[ReviewAnalysis]`.
3. После обработки **всех** батчей Шаг 2 завершён, и оркестратор передаёт `low_conf_queue` в Шаг 2.2 (см. `tasks/06_step22_reclassification.md`).
4. Результаты Шага 2.2 **замещают** соответствующие записи из Шага 2 в финальном списке.
5. Если после Шага 2.2 уверенность всё ещё низкая — отзыв помечается `low_confidence=True` и идёт в KPI с пометкой (см. Шаг 3).

**Важно:** в low-confidence очередь попадает **полная** запись `ReviewAnalysis` + текст отзыва (для повторного анализа). Не отдельные аспекты, а весь отзыв целиком.

```python
class LowConfItem(BaseModel):
    review_id: str
    text: str                          # text_normalized / text_translated
    initial_analysis: ReviewAnalysis   # что выдала mini-модель
    reason: str                        # почему попал в очередь
```

## API

```python
async def step2_process_batch(
    batch: list[NormalizedReview],
    llm: LLMClient,
) -> list[ReviewAnalysis]: ...

async def step2_run(
    reviews: list[NormalizedReview],
    runner: BatchRunner,
    llm: LLMClient,
    batch_size: int = 12,
    low_conf_threshold: float = 0.5,
) -> tuple[list[ReviewAnalysis], list[LowConfItem]]:
    """
    Возвращает:
    - high_conf_results: отзывы с уверенным анализом
    - low_conf_queue: отзывы для переклассификации в Шаге 2.2
    """
```

## Псевдокод

```python
async def step2_run(reviews, runner, llm, batch_size, low_conf_threshold):
    log = logger.bind(step="step2")
    log.info("Step 2 start", reviews_count=len(reviews), batch_size=batch_size)

    batches = [reviews[i:i+batch_size] for i in range(0, len(reviews), batch_size)]

    # ⚡ Параллельная обработка всех батчей
    batch_results: list[list[ReviewAnalysis]] = await runner.run_many(
        batches,
        worker=lambda b: step2_process_batch(b, llm),
    )

    # Разделяем по уверенности
    high_conf: list[ReviewAnalysis] = []
    low_conf_queue: list[LowConfItem] = []

    review_text_map = {r.review_id: (r.text_translated or r.text_normalized) for r in reviews}

    for analyses in batch_results:
        for a in analyses:
            needs_reclass, reason = needs_reclassification(a, low_conf_threshold)
            if needs_reclass:
                low_conf_queue.append(LowConfItem(
                    review_id=a.review_id,
                    text=review_text_map[a.review_id],
                    initial_analysis=a,
                    reason=reason,
                ))
                log.bind(review_id=a.review_id).info(
                    "Sent to low-conf queue",
                    reason=reason,
                    overall_confidence=a.overall_confidence,
                )
            else:
                high_conf.append(a)

    log.info(
        "Step 2 done",
        high_conf=len(high_conf),
        low_conf=len(low_conf_queue),
        low_conf_pct=round(100 * len(low_conf_queue) / max(1, len(reviews)), 1),
    )
    return high_conf, low_conf_queue
```

## Параметры конфига

```python
step2_batch_size: int = 12
step2_low_conf_threshold: float = 0.5
step2_max_concurrency: int = 8
```

## Логирование

- INFO старт: `reviews_count`, `batch_size`, `total_batches`.
- DEBUG на каждый батч: `batch_id`, `latency_ms`, `cost_usd`, `prompt_tokens`, `completion_tokens`.
- INFO на каждый отзыв в low-conf очереди: `review_id`, `reason`, `overall_confidence`.
- INFO итог: `high_conf=N`, `low_conf=M`, `low_conf_pct=X%`, `total_cost_usd=...`.

## Стоимость (на 10 000 отзывов)

- ~834 батча × (800 ток. вход + 400 ток. выход) ≈ 1M токенов ≈ **$0.39**
- С Batch API (50% скидка) ≈ **$0.20**

Расход на Шаг 2.2 (low-confidence) посчитан в `tasks/06`.

## Критерии готовности

- [ ] Батчи запускаются параллельно через `asyncio.gather` + семафор.
- [ ] Pydantic строго валидирует `BatchOutput`, instructor ретраит невалидный JSON.
- [ ] При размере батча > 20 — WARNING в лог (превышение рекомендованного лимита).
- [ ] Отзывы с `confidence < 0.5` (общий ИЛИ аспект) попадают в `low_conf_queue` целиком.
- [ ] В лог пишется доля low-conf от общего числа — нужно для мониторинга качества mini-модели.
- [ ] Юнит-тест: батч из 3 отзывов, 1 c overall_confidence=0.3 → попадает в low_conf.
- [ ] Юнит-тест: батч с одним отзывом, у которого аспект confidence=0.4 → попадает в low_conf, reason="low_aspect_confidence".
- [ ] При ошибке парсинга всего батча — батч ретраится; если 3 раза неудача — все отзывы из батча идут в low_conf_queue с reason="batch_parse_failed".

## Подсказки

- `instructor` вернёт `BatchOutput`, поля `items` ровно столько, сколько отзывов в батче. Если `len(items) != len(batch)` — это редкий, но возможный сбой; в таком случае ретраим батч с явным указанием в промпте «верни ровно N объектов».
- Чтобы не путать модель с длинными батчами — каждому отзыву в промпте присваиваем номер `[1]`, `[2]`, ... и просим возвращать `review_id` ровно как передано.
- Temperature=0.0.
