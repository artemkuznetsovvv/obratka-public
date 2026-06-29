# Задача 01: Async оркестратор и параллелизация батчей

## Цель

Запускать LLM-шаги пайплайна параллельно по батчам через `asyncio` + `aiohttp`. Контролировать конкурентность, ретраи и rate limit OpenRouter. Это сердце пайплайна — все остальные шаги на нём строятся.

> v2 / tasks/12: `run_pipeline` вызывает `setup_phoenix(settings)`, создаёт
> корневой `step_span("pipeline", ...)`, кладёт `trace_id` в loguru-контекст,
> прокидывает `ArtifactCollector` во все реализованные шаги и в конце вызывает
> `render_report(...)`, если `settings.report.enabled=true`. БД и кэш в этой
> итерации не реализуются.

## Файлы

- `src/obratka/orchestrator.py` — главный раннер пайплайна
- `src/obratka/llm/client.py` — async-клиент OpenRouter
- `src/obratka/llm/retry.py` — обёртка для ретраев и backoff

## Зависимости

```toml
aiohttp = "^3.9"
instructor = "^1.5"          # обёртка над openai-совместимым API + Pydantic
openai = "^1.40"             # instructor использует openai SDK; OpenRouter совместим
pydantic = "^2.7"
tenacity = "^8.3"            # ретраи с экспоненциальным backoff
asyncio-throttle = "^1.0"    # лимиты RPS (опционально)
```

## Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│                      orchestrator.py                        │
│                                                             │
│  run_id = uuid4()                                           │
│                                                             │
│  reviews ─→ Step0 (algo) ─→ Step0.5 (async batched)         │
│                              │                              │
│                              ↓                              │
│                          Step1 (async по авторам)           │
│                              │                              │
│                              ↓                              │
│                          Step2 (async batched, 10–15)       │
│                          ┌───┴────┐                         │
│                          │        │                         │
│                  conf≥0.5         conf<0.5 → LowConfQueue   │
│                          │        │                         │
│                          │        ↓                         │
│                          │   Step2.2 (GPT-4o, async)        │
│                          ↓        ↓                         │
│                          └───┬────┘                         │
│                              ↓                              │
│                          Step2.1 (1 вызов)                  │
│                              ↓                              │
│                          Step3 (algo)                       │
│                              ↓                              │
│                          Step4 (1 вызов, DeepSeek)          │
└─────────────────────────────────────────────────────────────┘
```

## Ключевые компоненты

### 1. `LLMClient` (llm/client.py)

Async-клиент через `instructor` поверх OpenAI SDK с `base_url=https://openrouter.ai/api/v1`.

```python
class LLMClient:
    def __init__(self, api_key: str, base_url: str, default_headers: dict): ...

    async def complete(
        self,
        *,
        model: str,
        messages: list[dict],
        response_model: type[BaseModel] | None = None,
        max_retries: int = 3,
        temperature: float = 0.0,
        timeout: float = 60.0,
        request_id: str | None = None,
    ) -> tuple[BaseModel | str, UsageInfo]:
        """Возвращает (распарсенный ответ, инфо о токенах/стоимости)."""
```

- Если `response_model` задан → используем `instructor.AsyncInstructor` для structured output с auto-retry.
- Если нет → возвращаем сырой текст.
- `UsageInfo`: `prompt_tokens`, `completion_tokens`, `cost_usd` (рассчитывается из конфига цен).
- На каждый вызов логируем: model, latency, токены, $, request_id.

### 2. Семафор конкурентности

```python
class BatchRunner:
    def __init__(self, llm: LLMClient, max_concurrency: int = 8):
        self._sem = asyncio.Semaphore(max_concurrency)

    async def run_many(
        self,
        items: list[T],
        worker: Callable[[T], Awaitable[R]],
    ) -> list[R]:
        async def guarded(item):
            async with self._sem:
                return await worker(item)
        return await asyncio.gather(*[guarded(i) for i in items], return_exceptions=False)
```

- `max_concurrency` — из конфига, по умолчанию `8`.
- На больших объёмах (>5К отзывов) — `12–16`.
- При получении 429 от OpenRouter — backoff (см. retry.py) и понижение лимита через `asyncio-throttle` (опционально).

### 3. Ретраи (llm/retry.py)

Через `tenacity`:

- 3 попытки на сетевые ошибки и 5xx.
- Экспоненциальный backoff: 1s → 2s → 4s + jitter.
- На 429 — читаем `Retry-After`, ждём указанное время.
- На ошибки парсинга JSON — отдельная ветка через `instructor` (он сам ретраит с указанием ошибки модели).

### 4. Главный пайплайн (orchestrator.py)

```python
async def run_pipeline(
    reviews: list[RawReview],
    business_id: int,
    config: Config,
) -> PipelineResult:
    run_id = uuid4()
    with logger.contextualize(run_id=str(run_id), business_id=business_id):
        logger.info("Pipeline start", total_reviews=len(reviews))

        # Шаг 0 — синхронный алгоритм
        normalized = step0_normalize(reviews)

        # Шаг 0.5 — только не-RU
        non_ru = [r for r in normalized if r.lang != "ru"]
        translated_map = await step05_translate(non_ru, batch_runner)
        # apply translated_map ...

        # Шаг 1 — по авторам (батч = автор и его отзывы)
        authors = group_by_author(normalized)
        fake_results = await batch_runner.run_many(authors, step1_check_author)
        clean = filter_fakes(normalized, fake_results)

        # Шаг 2 — батчи по 12 отзывов
        batches = chunk(clean, size=config.step2_batch_size)
        step2_results = await batch_runner.run_many(batches, step2_process_batch)
        topic_items, low_conf_queue = split_by_confidence(step2_results, threshold=0.5)

        # Шаг 2.2 — переклассификация низкоуверенных через GPT-4o
        if low_conf_queue:
            reclass_runner = BatchRunner(llm, max_concurrency=4)  # сильная модель → ниже параллелизм
            reclassified = await reclass_runner.run_many(low_conf_queue, step22_reclassify)
            topic_items = merge_reclassified(topic_items, reclassified)

        # Шаг 2.1 — кластеризация (один вызов)
        topic_map = await step21_cluster(topic_items)
        topic_items = apply_topic_map(topic_items, topic_map)

        # Шаг 3 — algo
        kpi = step3_aggregate(topic_items, fake_results, period=...)

        # Шаг 4 — один вызов DeepSeek
        recommendations = await step4_recommend(kpi, business_id)

        logger.info("Pipeline done",
                    total_cost_usd=llm.total_cost,
                    total_duration_s=...)
        return PipelineResult(...)
```

### 5. CLI

```bash
python -m obratka.orchestrator \
    --input reviews.json \
    --business-id 42 \
    --concurrency 8 \
    --output result.json
```

Парсинг через `argparse` или `typer`.

## Параметры конфига (config.py)

```python
class PipelineConfig(BaseModel):
    max_concurrency: int = 8
    step2_batch_size: int = 12
    step2_low_conf_threshold: float = 0.5
    step2_max_concurrency: int = 8
    step22_max_concurrency: int = 4   # сильная модель — снижаем
    request_timeout_s: float = 60.0
    max_retries: int = 3
```

## Критерии готовности

- [ ] При запуске на 1000 фейковых отзывов пайплайн отрабатывает без блокировок event loop.
- [ ] Все LLM-вызовы идут через единый `LLMClient`, нигде нет прямых обращений к openai/google sdk.
- [ ] При 429 от OpenRouter происходит корректный backoff, пайплайн не падает.
- [ ] При невалидном JSON `instructor` ретраит с подсказкой об ошибке.
- [ ] Семафор реально ограничивает конкурентность (проверить логами).
- [ ] `run_id` присутствует в каждой записи лога одного запуска.
- [ ] Подсчёт суммарной стоимости ($) по всем шагам корректен и логируется в конце.

## Подсказки

- Для теста параллелизма временно подменить `LLMClient.complete` на мок с `await asyncio.sleep(random.uniform(0.2, 1.0))`.
- `instructor.from_openai(AsyncOpenAI(base_url="https://openrouter.ai/api/v1", ...))` — рабочий способ подключения OpenRouter.
- Для отслеживания общего расхода — атрибут `total_cost` в `LLMClient`, инкрементируется атомарно через `asyncio.Lock`.
