# Задача 06: Шаг 2.2 — Переклассификация low-confidence через GPT-4o

> v2 / tasks/12: `step22_reclassify_all` принимает
> `collector: ArtifactCollector | None`, оборачивается в `step_span("step22")`
> и пишет `Step22Artifact` с before/after сэмплами, приростом confidence,
> сменами тональности и сменами набора тем.

## Цель

Обработать очередь отзывов с низкой уверенностью (`low_conf_queue` из Шага 2) сильной моделью. Получить более уверенный анализ тем и тональности.

## Файлы

- `src/obratka/steps/step22_reclassify.py`

## Модель

- **GPT-4o** через OpenRouter
- Цены: вход $2.50 / выход $10.00 за 1M токенов (≈ в 17× дороже mini)
- OpenRouter ID: `openai/gpt-4o`

## Когда вызывается

После Шага 2, только если `low_conf_queue` непуст. Ожидаемый объём — 5–15% от общего потока (зависит от качества данных).

## Pydantic-схемы

Используются те же, что в Шаге 2 (`Aspect`, `ReviewAnalysis`, `SENTIMENT`).

```python
class ReclassifyInput(BaseModel):
    review_id: str
    text: str
    initial_aspects: list[str]      # темы, что выделила mini-модель
    available_topics: list[str]     # базовый набор + кластеризованные свободные

class ReclassifyOutput(BaseModel):
    review_id: str
    is_mixed: bool
    overall_sentiment: SENTIMENT
    overall_confidence: float
    aspects: list[Aspect]
    low_confidence_final: bool      # если даже сильная модель не уверена
```

## Системный промпт (с few-shot)

```
Ты — точный аналитик отзывов. Предыдущая модель не была уверена в анализе этого отзыва.
Твоя задача — дать максимально точный анализ тем и тональности.

Используй темы только из списка available_topics, если они подходят.
Если ни одна не подходит — добавь свободную с is_freeform=true.

Особое внимание:
- Ирония и сарказм («ну просто гениально!»)
- Двусмысленные формулировки
- Смешанные отзывы с равными долями позитива и негатива
- Очень короткие отзывы — оценивай conservatively

Если даже после анализа уверенность < 0.5 — поставь low_confidence_final=true,
но всё равно выбери наиболее вероятный вариант.

Few-shot examples:
[пример 1: ироничный отзыв с правильным разбором]
[пример 2: смешанный отзыв]
[пример 3: короткий отзыв «нормально» → нейтральный, low_confidence_final=true]

Верни строго JSON по схеме.
```

В user-prompt передаём:
- review_id
- сам текст
- `initial_aspects` — темы, что выделила mini-модель (как подсказку, что искать)
- `available_topics` — базовый набор + список тем, что собраны на Шаге 2.1

## Параллелизация

- **Конкурентность ниже**, чем у Шага 2: `step22_max_concurrency = 4` по умолчанию.
- Причина — у GPT-4o ниже rate limit и выше стоимость, не хочется ловить 429.
- Каждый отзыв — отдельный LLM-вызов (не батчим, чтобы получить максимальное качество).

## API

```python
async def reclassify_one(
    item: LowConfItem,
    available_topics: list[str],
    llm: LLMClient,
) -> ReclassifyOutput: ...

async def step22_reclassify_all(
    queue: list[LowConfItem],
    available_topics: list[str],
    runner: BatchRunner,
    llm: LLMClient,
) -> list[ReviewAnalysis]:
    """
    Возвращает финальные ReviewAnalysis для всех отзывов из очереди.
    Эти результаты замещают записи в основном списке (по review_id).
    """
```

## Псевдокод

```python
async def step22_reclassify_all(queue, available_topics, runner, llm):
    log = logger.bind(step="step22")
    if not queue:
        log.info("No low-confidence reviews, skipping")
        return []

    log.info("Step 2.2 start", queue_size=len(queue))

    # Используем отдельный runner с пониженной конкурентностью
    strong_runner = BatchRunner(llm, max_concurrency=4)

    results: list[ReclassifyOutput] = await strong_runner.run_many(
        queue,
        worker=lambda item: reclassify_one(item, available_topics, llm),
    )

    # Конвертируем в ReviewAnalysis
    final = [
        ReviewAnalysis(
            review_id=r.review_id,
            is_mixed=r.is_mixed,
            overall_sentiment=r.overall_sentiment,
            overall_confidence=r.overall_confidence,
            aspects=r.aspects,
        )
        for r in results
    ]

    still_low = sum(1 for r in results if r.low_confidence_final)
    log.info(
        "Step 2.2 done",
        reclassified=len(results),
        still_low_confidence=still_low,
    )
    return final
```

## Слияние с результатами Шага 2

В оркестраторе:

```python
high_conf, low_conf_queue = await step2_run(reviews, ...)
reclassified = await step22_reclassify_all(low_conf_queue, available_topics, ...)

# Замещаем по review_id
final_analyses = high_conf + reclassified
# (review_id уникален, поэтому коллизий нет)
```

## Порядок относительно Шага 2.1

Логически Шаг 2.1 (кластеризация свободных тем) нужен до 2.2, чтобы передать в `available_topics` уже унифицированные темы. Но это создаёт зависимость:

**Рекомендованный порядок:**
1. Шаг 2 (mini, batched) — получаем сырые темы у уверенных + low-conf очередь.
2. Шаг 2.1 (один вызов, кластеризация всех уникальных тем из 2 + initial_aspects из low-conf).
3. Шаг 2.2 (GPT-4o) — получаем кластеризованные темы как `available_topics`.
4. Финальный merge.

Альтернатива (быстрее, но менее аккуратно): запускать 2.1 и 2.2 параллельно, потом во 2.2 пост-обработкой подменять темы по карте кластеризации. Решение по умолчанию — последовательный вариант.

## Стоимость (на 10К отзывов, 10% low-conf)

- ~1000 отзывов × (500 ток. вход + 200 ток. выход) ≈ 700K токенов
- 500K × $2.50/M + 200K × $10.00/M ≈ **$1.25 + $2.00 = $3.25**

⚠ В исходном ТЗ указано $0.50–1.50 на 10К — это при 5% low-conf и более коротких текстах. На практике планируй до **$3.25** в худшем случае.

## Логирование

- INFO старт: `queue_size`, `available_topics_count`.
- DEBUG на каждый отзыв: `review_id`, `prev_overall_confidence`, `new_overall_confidence`, `latency_ms`, `cost_usd`.
- WARNING если `low_confidence_final=True`: `review_id`, `final_confidence`.
- INFO итог: `reclassified`, `still_low_confidence`, `total_cost_usd`.

## Критерии готовности

- [ ] Шаг пропускается при пустой очереди (без ошибок).
- [ ] Используется отдельный `BatchRunner` с `max_concurrency=4`.
- [ ] Few-shot примеры подключены и помогают на иронии/сарказме (юнит-тесты на синтетике).
- [ ] Результаты корректно замещают записи Шага 2 по `review_id`.
- [ ] При `low_confidence_final=True` отзыв всё равно попадает в финальный список (с пометкой), не теряется.
- [ ] Юнит-тест: подаём `LowConfItem` с ироничным текстом — модель меняет тональность.

## Подсказки

- Можно сделать гибрид: для batch_parse_failed (целый батч сломался на парсинге) тоже отправлять в Шаг 2.2 — там вызовы поодиночке, парсинг надёжнее.
- Для дальнейшей экономии — кэш по `(text_hash, prompt_version, model)` тоже работает на Шаге 2.2.
