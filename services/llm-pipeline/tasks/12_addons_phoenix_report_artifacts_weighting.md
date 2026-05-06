# Доработки v2 — Phoenix, HTML-отчёт, артефакты этапов, веса отзывов

> Этот документ — **дополнение** к существующему пакету (`CLAUDE.md` + `tasks/00..11`).
> Файл переопределяет/дополняет ряд решений; в случае конфликта **приоритет у этого файла**.

## Содержание

1. [Логирование через Arize Phoenix](#1-логирование-через-arize-phoenix)
2. [HTML-отчёт (временная фича, легко выпиливается)](#2-html-отчёт-временная-фича-легко-выпиливается)
3. [Артефакты каждого этапа в отчёте](#3-артефакты-каждого-этапа-в-отчёте)
4. [Веса старых отзывов (time decay)](#4-веса-старых-отзывов-time-decay)
5. [Изменения в существующих задачах](#5-изменения-в-существующих-задачах)

---

## 1. Логирование через Arize Phoenix

### Что меняется

Раньше: loguru с 4 sinks, включая JSONL.
Теперь: loguru остаётся (консоль + текстовый файл + errors.log), **JSONL заменяется на трейсы в Arize Phoenix через OpenTelemetry + OpenInference**.

Phoenix даёт UI для просмотра LLM-вызовов: дерево спанов, токены, латентность, стоимость, входные/выходные сообщения. Это сильно удобнее для отладки промптов, чем grep по JSONL.

### Файлы

- `src/obratka/observability/phoenix_setup.py` — инициализация трейсера
- `src/obratka/observability/spans.py` — хелперы для ручных спанов шагов
- `docker-compose.phoenix.yml` — локальный Phoenix
- Правка `src/obratka/logging_setup.py` — убираем JSONL sink

### Зависимости

```toml
arize-phoenix = "^5.0"
arize-phoenix-otel = "^0.6"
openinference-instrumentation-openai = "^0.1"
opentelemetry-api = "^1.27"
opentelemetry-sdk = "^1.27"
opentelemetry-exporter-otlp = "^1.27"
```

### Запуск Phoenix

**Локально (dev), Docker:**

```yaml
# docker-compose.phoenix.yml
services:
  phoenix:
    image: arizephoenix/phoenix:latest
    ports:
      - "6006:6006"   # UI
      - "4318:4317"   # OTLP gRPC (host 4318 -> container 4317)
    environment:
      PHOENIX_WORKING_DIR: /mnt/phoenix
    volumes:
      - phoenix-data:/mnt/phoenix
volumes:
  phoenix-data:
```

`docker compose -f docker-compose.phoenix.yml up -d` → UI на http://localhost:6006

**Self-hosted (prod):**
Тот же образ за reverse proxy, OTLP endpoint указывается через `PHOENIX__OTLP_ENDPOINT`.

### .env

```dotenv
# Включает/выключает экспорт трейсов
PHOENIX__ENABLED=true

# Адрес коллектора (gRPC)
PHOENIX__OTLP_ENDPOINT=http://localhost:4318

# Имя проекта в Phoenix UI (разделяет dev/staging/prod)
PHOENIX__PROJECT_NAME=obratka-dev

# Опционально: API key для Phoenix Cloud
PHOENIX__API_KEY=
```

### Инициализация (`observability/phoenix_setup.py`)

```python
from phoenix.otel import register
from openinference.instrumentation.openai import OpenAIInstrumentor
from obratka.config import settings

_tracer_provider = None

def setup_phoenix() -> None:
    """Вызывается один раз в orchestrator перед стартом пайплайна."""
    global _tracer_provider
    if not settings.phoenix.enabled:
        return
    if _tracer_provider is not None:
        return  # идемпотентность

    _tracer_provider = register(
        project_name=settings.phoenix.project_name,
        endpoint=settings.phoenix.otlp_endpoint,
        protocol="grpc",
        # Если Phoenix Cloud:
        headers={"api_key": settings.phoenix.api_key} if settings.phoenix.api_key else None,
    )

    # Авто-инструментирование openai SDK (его использует instructor под капотом)
    OpenAIInstrumentor().instrument(tracer_provider=_tracer_provider)


def get_tracer(name: str):
    from opentelemetry import trace
    return trace.get_tracer(name)
```

### Что попадает в Phoenix автоматически

`OpenAIInstrumentor` ловит **все** вызовы через `openai` SDK (а через него работает и `instructor`, и наш `LLMClient`):

- модель, prompt, completion, токены вход/выход, латентность,
- system_prompt и user-сообщения,
- ошибки (timeout, 429, парсинг JSON),
- автоматические ретраи instructor.

Поскольку OpenRouter — OpenAI-совместимый API, инструментирование работает как для нативного OpenAI: `base_url=https://openrouter.ai/api/v1`, всё ловится.

### Ручные спаны на уровне шагов

Для иерархии «pipeline → step → batch → llm-call» добавляем кастомные спаны.

```python
# observability/spans.py
from contextlib import asynccontextmanager
from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode

tracer = trace.get_tracer("obratka")

@asynccontextmanager
async def step_span(step_name: str, **attrs):
    """Спан вокруг шага пайплайна."""
    with tracer.start_as_current_span(f"step.{step_name}") as span:
        for k, v in attrs.items():
            span.set_attribute(k, v)
        try:
            yield span
        except Exception as e:
            span.set_status(Status(StatusCode.ERROR, str(e)))
            span.record_exception(e)
            raise

@asynccontextmanager
async def batch_span(step_name: str, batch_id: str, batch_size: int):
    with tracer.start_as_current_span(f"batch.{step_name}") as span:
        span.set_attribute("batch.id", batch_id)
        span.set_attribute("batch.size", batch_size)
        yield span
```

### Использование в шагах

```python
async def step2_run(reviews, runner, llm, ...):
    async with step_span("step2", reviews_count=len(reviews)) as span:
        # ... обработка батчей ...
        span.set_attribute("step2.high_conf_count", len(high_conf))
        span.set_attribute("step2.low_conf_count", len(low_conf_queue))
        span.set_attribute("step2.cost_usd", llm.last_step_cost)
```

В `BatchRunner` тоже оборачиваем каждый батч:

```python
async def _process_one(item, idx):
    async with batch_span("step2", f"batch_{idx:04d}", len(item)):
        return await worker(item)
```

### run_id ↔ trace

Чтобы Phoenix-трейсы и loguru-логи можно было сопоставить:

1. На старте пайплайна корневой спан создаётся с атрибутом `obratka.run_id = run_id`.
2. В loguru запись `run_id` уже идёт через `contextualize` (см. `tasks/00`).
3. Через `trace.get_current_span().get_span_context().trace_id` получаем `trace_id` и тоже пишем его в loguru-контекст.

```python
async def run_pipeline(...):
    setup_phoenix()
    run_id = str(uuid4())

    async with step_span("pipeline", **{"obratka.run_id": run_id, "business_id": business_id}):
        trace_id = format(trace.get_current_span().get_span_context().trace_id, "032x")
        with logger.contextualize(run_id=run_id, trace_id=trace_id):
            logger.info("Pipeline start", ...)
            # ...
```

Теперь по `trace_id` из лога можно открыть конкретный трейс в Phoenix UI.

### Атрибуты для Phoenix-спанов (рекомендованный набор)

| Спан | Атрибуты |
|---|---|
| `pipeline` | `obratka.run_id`, `business_id`, `total_reviews`, `total_cost_usd` |
| `step.step0` | `reviews_count`, `non_ru_count`, `duration_ms` |
| `step.step05` | `translated_count`, `cached_count`, `cost_usd` |
| `step.step1` | `authors_count`, `suspicious_count`, `cost_usd` |
| `step.step2` | `reviews_count`, `batches_count`, `high_conf_count`, `low_conf_count`, `cost_usd` |
| `step.step22` | `queue_size`, `still_low_conf_count`, `cost_usd` |
| `step.step21` | `unique_topics_in`, `clusters_out`, `cost_usd` |
| `step.step3` | `pain_points_count`, `nps_score` |
| `step.step4` | `recommendations_count`, `cost_usd` |
| `batch.step2` | `batch.id`, `batch.size`, `low_conf_count` |

OpenInference-спаны (LLM-вызовы) приходят автоматически с правильными атрибутами `llm.*` — их вручную не настраиваем.

### Правка `logging_setup.py`

Удаляем JSONL sink — его роль теперь у Phoenix. Остаются:
- консоль (INFO+),
- `logs/obratka.log` (DEBUG+, ротация 50MB / 14 дней),
- `logs/errors.log` (ERROR+ со stacktrace, 90 дней).

Если `PHOENIX__ENABLED=false` — Phoenix не инициализируется, проект работает с одним только loguru. Это страховка на случай, если Phoenix недоступен.

### Критерии готовности

- [ ] При `PHOENIX__ENABLED=true` все LLM-вызовы видны в Phoenix UI с моделью, токенами, prompt, completion.
- [ ] Иерархия спанов: `pipeline → step.X → batch.Y → openai.chat.completion`.
- [ ] `obratka.run_id` присутствует на корневом спане; по нему можно отфильтровать запуск.
- [ ] `trace_id` из Phoenix присутствует в каждой записи loguru одного запуска.
- [ ] При недоступности Phoenix (контейнер выключен) пайплайн **не падает** — экспорт молча фейлится, loguru работает.
- [ ] При `PHOENIX__ENABLED=false` нет попыток коннекта к OTLP endpoint.

### Подсказки

- Phoenix хранит трейсы локально в SQLite/postgres — для прода настрой Postgres-бэкенд.
- Для отслеживания стоимости в Phoenix UI: записывай `llm.token_count.prompt`, `llm.token_count.completion` через OpenInference семантику — `OpenAIInstrumentor` это делает сам.
- Если в проекте появится несколько LLM-провайдеров — для каждого свой OpenInference-инструментор (anthropic, google, etc.). Сейчас всё через openai SDK + OpenRouter, поэтому хватает одного.

---

## 2. HTML-отчёт (временная фича, легко выпиливается)

### Цель

Дать человекочитаемый просмотр результата пайплайна. Сейчас — для разработки и демо. В проде это уйдёт в нормальный фронтенд. Поэтому фича должна **изолированно** жить в одном модуле и удаляться одной командой.

### Принцип «легко выпилить»

Вся фича — в одной папке `src/obratka/report/`. Чтобы выключить: удалить папку и вызов `render_report(...)` из `orchestrator.py`. Никаких зависимостей в других шагах.

В `config.py` — флаг:

```python
class ReportConfig(BaseModel):
    enabled: bool = True
    output_dir: str = "reports"
    open_in_browser: bool = False    # автооткрытие после генерации
```

`REPORT__ENABLED=false` — фича не запускается, файл не создаётся.

### Файлы

```
src/obratka/report/
├── __init__.py
├── builder.py          # сборка HTML из PipelineResult + артефактов этапов
├── templates/
│   ├── report.html.j2  # один Jinja-шаблон
│   └── styles.css.j2   # инлайнится в HTML
└── README.md           # «как удалить эту фичу»
```

### Зависимости

```toml
jinja2 = "^3.1"
```

Никакого FastAPI, Streamlit, React — только статический HTML с инлайн CSS/JS. Всё в одном файле, открывается двойным кликом.

### Что в отчёте

Структура страницы (одна вертикальная лента, вкладки опциональны):

```
┌─────────────────────────────────────────────────────────┐
│  Шапка: бизнес, период, run_id, ссылка в Phoenix        │
├─────────────────────────────────────────────────────────┤
│  Core KPI: рейтинг, NPS, динамика, доля негатива        │
│  (большие числа + sparkline)                            │
├─────────────────────────────────────────────────────────┤
│  Болевые точки: топ-N карточек с цитатами               │
├─────────────────────────────────────────────────────────┤
│  Рекомендации: 3 группы (strategic/tactical/comm)       │
├─────────────────────────────────────────────────────────┤
│  Артефакты этапов (collapsible):                        │
│    ▶ Шаг 0: нормализация                                │
│    ▶ Шаг 0.5: перевод                                   │
│    ▶ Шаг 1: фейки                                       │
│    ▶ Шаг 2: темы (high/low conf)                        │
│    ▶ Шаг 2.2: переклассификация                         │
│    ▶ Шаг 2.1: кластеризация                             │
│    ▶ Шаг 3: KPI                                         │
│    ▶ Шаг 4: рекомендации                                │
├─────────────────────────────────────────────────────────┤
│  Footer: стоимость, длительность, версии промптов       │
└─────────────────────────────────────────────────────────┘
```

Содержимое каждого этапа описано в разделе 3 ниже.

### Графика

Только инлайн SVG и vanilla JS — никаких внешних библиотек по умолчанию.

Если хочется чартов — подключаем **Chart.js через CDN** (один `<script src=...>`). Один раз. Графики:
- динамика рейтинга по неделям/месяцам,
- доля негатива по темам (бар),
- распределение тональностей (donut),
- сравнение «весь период» vs «свежие» (см. раздел 4 про веса).

Чтобы не зависеть от интернета — есть флаг `chartjs_offline=False`. Если `True` — Chart.js встраивается в HTML инлайном (~250 KB).

### API

```python
# src/obratka/report/builder.py
from pathlib import Path

def render_report(
    pipeline_result: PipelineResult,
    stage_artifacts: StageArtifacts,    # см. раздел 3
    output_dir: str | Path = "reports",
    open_in_browser: bool = False,
) -> Path:
    """
    Создаёт <output_dir>/<run_id>.html и возвращает путь.
    """
```

Вызов в оркестраторе:

```python
if settings.report.enabled:
    from obratka.report.builder import render_report
    path = render_report(result, stage_artifacts, settings.report.output_dir)
    logger.info("Report generated", path=str(path))
    if settings.report.open_in_browser:
        import webbrowser; webbrowser.open(path.as_uri())
```

Импорт **внутри if** — если папку `report/` удалить, флаг останется `False` дефолтом, ничего не сломается.

### Стиль

- Тёмная тема по умолчанию + переключатель (один CSS-варс).
- Шрифты системные (никаких Google Fonts — проект работает без интернета).
- CSS Grid для layout, минимум классов.
- Все цифры — большие, читаемые. Цветовая семантика: зелёный = позитив, красный = негатив, жёлтый = warning/low-confidence.

### Имя файла

`reports/<run_id>.html` + симлинк/копия `reports/latest.html` для удобства.

### Безопасность

- В отчёт могут попасть тексты отзывов с PII. Не публиковать наружу как есть.
- Атрибут `<meta name="robots" content="noindex">`.
- Если в `BusinessContext.custom_notes` есть конфиденциалка — не выводить (есть флаг `include_business_notes`).

### Удаление фичи (для прода)

```bash
# 1. Удалить флаг и импорт из orchestrator.py
# 2. Удалить папку
rm -rf src/obratka/report/
# 3. Удалить jinja2 из pyproject.toml
# 4. Удалить REPORT__* переменные из .env
```

В `src/obratka/report/README.md` зафиксировать эти 4 шага и список мест, где фича упоминается. Это «контракт на демонтаж».

### Критерии готовности

- [ ] Один HTML-файл, открывается двойным кликом, отображается без интернета (если `chartjs_offline=True`).
- [ ] При `REPORT__ENABLED=false` — файл не создаётся, ошибок нет.
- [ ] Удаление папки `src/obratka/report/` + флага не ломает остальной пайплайн (юнит-тест: запустить пайплайн с моком и проверить, что нет ImportError).
- [ ] Файл `report/README.md` содержит инструкцию по удалению фичи.
- [ ] Smoke-тест: подать в `render_report` фикстуру `PipelineResult` → получить HTML, проверить что валидный (через `html.parser` без ошибок).

---

## 3. Артефакты каждого этапа в отчёте

### Цель

Показать в HTML-отчёте, что именно произошло на каждом этапе пайплайна. Не только итоговые KPI, а **промежуточные данные**, чтобы:
- видеть, где модель ошибается,
- видеть, какие отзывы попали в low-conf и как изменились после Шага 2.2,
- легко находить плохие батчи и подкручивать промпты.

### Что меняется в архитектуре

В оркестраторе появляется аккумулятор `StageArtifacts`. Каждый шаг возвращает свой результат **И** свою сводку для отчёта. Сводка не блокирует прод — это side-channel, при выключенном отчёте не собирается.

### Pydantic-схемы (новый файл `src/obratka/report/artifacts.py`)

```python
from pydantic import BaseModel
from datetime import datetime

class StageStats(BaseModel):
    """Базовая статистика для любого шага."""
    name: str
    started_at: datetime
    finished_at: datetime
    duration_ms: int
    cost_usd: float = 0.0
    model: str | None = None
    prompt_version: str | None = None

class Step0Artifact(BaseModel):
    stats: StageStats
    input_count: int
    empty_after_norm_count: int       # отзывы, ставшие пустыми
    lang_distribution: dict[str, int] # {"ru": 850, "en": 120, "kk": 30}
    samples: list[NormalizationSample]  # 5–10 примеров до/после

class NormalizationSample(BaseModel):
    review_id: str
    text_raw: str
    text_normalized: str
    lang: str

class Step05Artifact(BaseModel):
    stats: StageStats
    translated_count: int
    cached_count: int
    failed_count: int
    samples: list[TranslationSample]   # 5 примеров

class TranslationSample(BaseModel):
    review_id: str
    source_lang: str
    text_original: str
    text_ru: str

class Step1Artifact(BaseModel):
    stats: StageStats
    authors_checked: int
    suspicious_authors: int
    excluded_reviews: int
    fakes_share: float
    confidence_distribution: dict[str, int]  # buckets: "0-0.3", "0.3-0.6", "0.6-1.0"
    samples: list[FakeSample]   # 5 подозрительных + 5 чистых

class FakeSample(BaseModel):
    author_id: str
    suspicious: bool
    confidence: float
    reason: str
    review_count: int
    sample_texts: list[str]    # до 3 текстов

class Step2Artifact(BaseModel):
    stats: StageStats
    batches_count: int
    avg_batch_latency_ms: int
    high_conf_count: int
    low_conf_count: int
    low_conf_pct: float
    confidence_histogram: list[int]   # 10 бакетов 0.0..1.0
    sentiment_distribution: dict[str, int]
    topics_distribution: dict[str, int]    # топ-20 тем по упоминаниям
    freeform_topics_count: int             # тем вне базового набора
    parse_failures: int                    # сколько батчей сломалось
    samples_high_conf: list[ReviewAnalysisSample]   # 5
    samples_low_conf: list[ReviewAnalysisSample]    # 10 (это самое полезное для отладки)

class ReviewAnalysisSample(BaseModel):
    review_id: str
    text: str
    overall_sentiment: str
    overall_confidence: float
    aspects: list[dict]   # [{topic, sentiment, confidence, fragment}]

class Step22Artifact(BaseModel):
    stats: StageStats
    queue_size: int
    reclassified_count: int
    still_low_conf_count: int
    confidence_delta_avg: float        # среднее улучшение confidence
    sentiment_changes: int             # сколько раз изменилась тональность
    topic_changes: int                 # сколько раз изменился набор тем
    samples: list[ReclassificationSample]   # 10 пар «до/после»

class ReclassificationSample(BaseModel):
    review_id: str
    text: str
    before: ReviewAnalysisSample       # из mini
    after: ReviewAnalysisSample        # из gpt-4o
    improvement: float                 # confidence_after - confidence_before
    flipped_sentiment: bool

class Step21Artifact(BaseModel):
    stats: StageStats
    unique_freeform_topics_in: int
    clusters_out: int
    mapping: dict[str, str]            # полная карта (если ≤50 тем, иначе обрезаем)
    canonical_topics: list[str]

class Step3Artifact(BaseModel):
    stats: StageStats
    core_kpi: dict                     # из CoreKPI
    loyalty: dict
    pain_points_top: list[dict]        # топ-10 болевых точек
    positive_points_top: list[dict]    # топ-10 позитивных черт
    trends: dict
    fake_stats: dict
    # Дополнительные срезы (см. раздел 4 про веса):
    weighted_kpi: dict | None = None
    fresh_window_kpi: dict | None = None

class Step4Artifact(BaseModel):
    stats: StageStats
    recommendations_count: int
    by_type: dict[str, int]            # {"strategic": 4, "tactical": 5, "communication": 2}
    summary: str
    full_recommendations: list[dict]

class StageArtifacts(BaseModel):
    """Контейнер всех артефактов для одного запуска."""
    run_id: str
    business_id: int
    started_at: datetime
    finished_at: datetime
    total_cost_usd: float
    phoenix_trace_id: str | None = None    # ссылка в Phoenix UI

    step0: Step0Artifact | None = None
    step05: Step05Artifact | None = None
    step1: Step1Artifact | None = None
    step2: Step2Artifact | None = None
    step22: Step22Artifact | None = None
    step21: Step21Artifact | None = None
    step3: Step3Artifact | None = None
    step4: Step4Artifact | None = None
```

### Как собираются артефакты

Каждый шаг получает опциональный `collector: ArtifactCollector | None`. Если `None` — шаг работает быстрее, никаких сэмплов не сохраняет. Если `not None` — пишет туда сводку.

```python
class ArtifactCollector:
    def __init__(self):
        self.artifacts = StageArtifacts(...)

    def record_step0(self, art: Step0Artifact): self.artifacts.step0 = art
    def record_step05(self, art: Step05Artifact): self.artifacts.step05 = art
    # ... и так далее
```

В оркестраторе:

```python
collector = ArtifactCollector(run_id=run_id, business_id=business_id) if settings.report.enabled else None

normalized = step0_normalize(reviews, collector=collector)
# ...
result = step3_aggregate(..., collector=collector)
recs = await step4_recommend(..., collector=collector)

if collector is not None:
    render_report(result, collector.artifacts, settings.report.output_dir)
```

### Сэмплирование

Чтобы артефакты не раздулись на 15К отзывов, отбираем сэмплы:

| Шаг | Что сэмплируем | Сколько |
|---|---|---|
| Step 0 | случайные нормализованные | 10 |
| Step 0.5 | случайные переводы | 5 |
| Step 1 | топ подозрительных + случайные чистые | 5 + 5 |
| Step 2 high-conf | случайные с разными тональностями | 5 |
| Step 2 low-conf | **все** до 50, потом случайные | 10–50 |
| Step 2.2 | все, у кого изменилась тональность + случайные | до 20 |
| Step 2.1 | весь mapping если ≤50 тем | до 50 |

Конфиг `report.max_samples_per_step` управляет лимитом.

### Что показывается в HTML по каждому шагу

**Шаг 0 (Нормализация)** — сводка + таблица «до/после» для 10 примеров + распределение языков (donut chart).

**Шаг 0.5 (Перевод)** — таблица оригинал/перевод для 5 примеров, кэш-хиты, ошибки переводов.

**Шаг 1 (Фейки)** — список топ-5 подозрительных авторов с reason, гистограмма confidence по решениям, доля исключённых отзывов.

**Шаг 2 (Темы + тональность)** — главное! 
- Гистограмма confidence (10 бакетов).
- Топ-20 тем с долей негатива (бар-чарт).
- Donut «high-conf vs low-conf».
- Таблица 10 low-confidence отзывов с подсветкой проблемных аспектов (красным confidence < 0.5).
- Sentiment distribution.

**Шаг 2.2 (Переклассификация)** — пары «до/после»: текст отзыва, рядом два блока — что выдала mini, что выдал gpt-4o. Подсветка изменившихся полей. Метрики: средний прирост confidence, сколько раз поменялась тональность.

**Шаг 2.1 (Кластеризация)** — таблица mapping (исходная тема → каноническая), список итоговых кластеров.

**Шаг 3 (KPI)** — те же KPI, что в шапке, но с детализацией: динамика по бинам, полный список болевых точек и позитивных черт, статистика фейков. **Также блок «весовые KPI vs обычные KPI»** (см. раздел 4).

**Шаг 4 (Рекомендации)** — все рекомендации с full body, отсортированные по приоритету и сгруппированные по типу.

### Раскрытие/скрытие

Каждый шаг — `<details><summary>Шаг N: ...</summary>...</details>`. По умолчанию свёрнуты, кроме Шага 2 и Шага 3 (главные для аналитики).

### Ссылки в Phoenix

Если `phoenix_trace_id` есть — на каждом шаге кнопка «Открыть в Phoenix» с deeplink:
`http://localhost:6006/projects/<project>/traces/<trace_id>`

Конкретный URL шаблон тоже в конфиге, чтобы для self-hosted можно было заменить.

### Критерии готовности

- [ ] При `REPORT__ENABLED=false` `ArtifactCollector` не создаётся, шаги не делают лишней работы по сбору сэмплов.
- [ ] При `REPORT__ENABLED=true` HTML содержит секции по всем 8 шагам с непустыми данными (на тестовом датасете).
- [ ] Размер HTML на 1000 отзывов ≤ 2 МБ (без инлайн Chart.js).
- [ ] Сэмплы Шага 2.2 (до/после) визуально читаемы — текст, два блока разбора рядом, подсветка изменений.
- [ ] Юнит-тест: коллектор с фикстурами → render → HTML парсится, содержит ожидаемые секции.

---

## 4. Веса старых отзывов (time decay)

### Цель

Свежие отзывы должны влиять на KPI и болевые точки **сильнее**, чем годовалые. Старый негатив про официанта, который уже уволился, не должен тянуть NPS вниз так же сильно, как вчерашний.

### Формула

Экспоненциальное затухание по половинному периоду (half-life):

```
weight(review) = 0.5 ^ (age_days / half_life_days)
```

- `half_life_days = 90` по умолчанию (через 90 дней вес становится 0.5, через 180 — 0.25, через год — ~0.06).
- Минимальный вес `weight_floor = 0.05` — старый отзыв не «исчезает» полностью, всегда даёт хотя бы 5% голоса. Иначе годовалая статистика будет нулевой и сравнения «старый vs новый» сломаются.

```python
import math

def time_decay_weight(
    review_date: datetime,
    reference_date: datetime,
    half_life_days: float = 90.0,
    weight_floor: float = 0.05,
) -> float:
    age_days = max(0.0, (reference_date - review_date).total_seconds() / 86400.0)
    w = 0.5 ** (age_days / half_life_days)
    return max(weight_floor, w)
```

`reference_date` — момент генерации отчёта (`now`), либо `period_end` если хотим воспроизводимости.

### Альтернативные функции (опционально)

В конфиге выбор стратегии:

```python
class WeightingStrategy(str, Enum):
    none = "none"                # старая логика, веса = 1
    exp_half_life = "exp"        # экспоненциальное затухание (default)
    linear_window = "linear"     # линейное от 1 до floor за `window_days`
    step = "step"                # 1.0 в окне fresh_window, weight_floor вне его
```

`exp` — рекомендованный дефолт. `step` — самый «жёсткий», только свежие учитываются.

### Параметры конфига

```python
class WeightingConfig(BaseModel):
    strategy: WeightingStrategy = WeightingStrategy.exp_half_life
    half_life_days: float = 90.0
    weight_floor: float = 0.05
    fresh_window_days: int = 30        # для отдельной "свежей" метрики, см. ниже
    enabled: bool = True
```

`PIPELINE__WEIGHTING__HALF_LIFE_DAYS=60` через env.

### Где применяются веса

Только в **Шаге 3 (Агрегация KPI)**. Шаги 1, 2, 2.2 работают со всеми отзывами одинаково — мы не отбрасываем старые, только взвешиваем при подсчёте метрик.

Метрики, которые считаются с весами:
- `avg_rating` → взвешенное среднее звёзд (`Σ(stars*w) / Σ(w)`).
- `negative_share`, `positive_share`, `mixed_share` → взвешенные доли.
- `loyalty.score` (NPS-подобный) → взвешенный.
- `pain_points[*].negative_share` → взвешенная доля негатива по теме.
- `pain_points[*].mention_count` — оставляем сырым (это счётчик упоминаний, веса не нужны), **но добавляем поле `weighted_mention_count`** для ранжирования.
- `positive_points[*].positive_share` → взвешенная доля позитива по теме.
- `positive_points[*].weighted_positive_mention_count` → для ранжирования сильных сторон.

Метрики, которые считаются БЕЗ весов (всегда):
- Динамика по периодам (`trends`) — там веса бессмысленны, бины уже разделяют свежее и старое.
- `fake_stats` — статистика по фактам, не по «значимости».

### Двойной отчёт: weighted vs raw

В отчёт идут **обе версии**:

```python
class PipelineResult(BaseModel):
    # Сохраняем текущий публичный API без CoreKPIWrapper:
    core_kpi: CoreKPI                  # raw, без весов
    core_kpi_weighted: CoreKPI | None  # weighted, None если weighting.enabled=False
    core_kpi_fresh: CoreKPI | None     # только последние fresh_window_days
    loyalty: LoyaltyIndex
    loyalty_weighted: LoyaltyIndex | None
    loyalty_fresh: LoyaltyIndex | None
```

Пользователь в HTML видит большие цифры **weighted**, а рядом маленьким серым — `raw` с пометкой «без учёта возраста» и `fresh` с пометкой «за последние 30 дней». Это даёт прозрачность: понятно, насколько веса влияют.

### Болевые точки с весами

Критерий «болевая точка» меняется:

```
is_pain = (weighted_negative_share > 0.20)
          OR (negative_mention_count >= 3)
          OR (weighted_negative_mention_count >= 2.0)
          OR (recent_growth > 50%)
         AND (mention_count >= 5)
```

`weighted_negative_share` смещён в сторону свежих → старые проблемы, по которым уже давно не жалуются, естественно вылетают из топа.
Абсолютный объём негатива нужен, чтобы темы с большим числом позитивных
упоминаний не скрывали несколько важных негативных сигналов.

### Позитивные черты с весами

Позитивные черты считаются симметрично болевым точкам:

```
is_positive_point = (weighted_positive_share > 0.50)
                    OR (positive_mention_count >= 5)
                    AND (mention_count >= 5)
```

В `PositivePoint` входят доля позитива, количество позитивных упоминаний,
взвешенные счётчики, цитаты, даты сэмплов и средний возраст упоминаний.

`recent_growth` сравнивает последние 30 дней (без весов) с предыдущими 30. Это ортогональная веса метрика, она ловит резкие сдвиги.

В `PainPoint`:

```python
class PainPoint(BaseModel):
    topic: str
    negative_share_raw: float
    negative_share_weighted: float
    mention_count: int
    weighted_mention_count: float
    growth_pct_30d: float | None
    sample_fragments: list[str]
    sample_dates: list[datetime]    # даты сэмплов — чтобы видеть свежесть
    avg_age_days: float             # средний возраст упоминаний (свежесть болевой точки)
```

`avg_age_days` важен: если средний возраст упоминаний >180 дней — это «старая боль», возможно уже решённая. В HTML такие подсвечиваются жёлтым.

### Влияние на Шаг 4 (Рекомендации)

В промпт DeepSeek передаётся **взвешенный** срез + отдельно `fresh_window` срез.
Рекомендации строятся только на основе часто повторяющихся недостатков:
минимум 3 негативных упоминания или `weighted_negative_mention_count >= 2.0`
при `mention_count >= 5`. Единичные жалобы передаются как ignored/weak signals
и не должны становиться самостоятельными рекомендациями.

В системном промпте добавляется:

```
Не используй проценты, знак % или точные числовые прогнозы в expected_impact.
Expected impact должен быть качественным или операционным: меньше повторяющихся
жалоб, быстрее обработка очередей, выше предсказуемость сервиса.

Используй weighted KPI для приоритизации.
Если есть резкое расхождение между weighted и fresh — это признак тренда:
- weighted хуже fresh → проблемы решаются, но старый осадок ещё тянет
- weighted лучше fresh → новый негатив, нужно срочно разбираться
```

### Псевдокод (Шаг 3 с весами)

```python
def aggregate_kpi(analyses, raw_reviews, fake_verdicts, business_id, settings) -> PipelineResult:
    ref_date = datetime.now()
    cfg = settings.pipeline.weighting

    review_by_id = {r.review_id: r for r in raw_reviews}

    if cfg.enabled and cfg.strategy != WeightingStrategy.none:
        weights = {
            r.review_id: time_decay_weight(
                r.posted_at, ref_date, cfg.half_life_days, cfg.weight_floor,
            )
            for r in raw_reviews
        }
    else:
        weights = {r.review_id: 1.0 for r in raw_reviews}

    # Сырые метрики
    raw_kpi = compute_core_kpi(analyses, raw_reviews, weights=None)

    # Взвешенные метрики
    weighted_kpi = compute_core_kpi(analyses, raw_reviews, weights=weights) if cfg.enabled else None

    # Свежий срез — без весов, но только за последние N дней
    fresh_cutoff = ref_date - timedelta(days=cfg.fresh_window_days)
    fresh_analyses = [a for a in analyses if review_by_id[a.review_id].posted_at >= fresh_cutoff]
    fresh_kpi = compute_core_kpi(fresh_analyses, raw_reviews, weights=None)

    pain_points = compute_pain_points(analyses, raw_reviews, weights, ref_date)
    positive_points = compute_positive_points(analyses, raw_reviews, weights, ref_date)
    # ...
    return PipelineResult(
        core_kpi=raw_kpi,
        core_kpi_weighted=weighted_kpi,
        core_kpi_fresh=fresh_kpi,
        pain_points=pain_points,
        positive_points=positive_points,
        ...
    )
```

### Параметры подбора half_life

Не универсальная константа, зависит от типа бизнеса и темпа изменений:

| Тип бизнеса | Рекомендованный half_life |
|---|---|
| Рестораны, кафе (быстрая ротация качества) | 60 дней |
| Клиники, медцентры | 90 дней (default) |
| Магазины, ритейл | 90 дней |
| Гостиницы (сезонность, медленные перемены) | 120 дней |
| Услуги (юристы, репетиторы) | 180 дней |

В `BusinessContext` опционально пробрасывать `half_life_override`.

### Отображение в HTML

В шапке отчёта три «карточки» рядом:

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ ВЗВЕШЕННЫЙ   │  │  СЫРОЙ       │  │ СВЕЖИЙ (30д) │
│   NPS = 38   │  │   NPS = 42   │  │   NPS = 28   │
│   ★ 4.2      │  │   ★ 4.3      │  │   ★ 4.0      │
│ neg 24%      │  │  neg 22%     │  │  neg 31%     │
└──────────────┘  └──────────────┘  └──────────────┘
            ▲ основное              ▲ тренд (хуже!)
```

Подсказка пользователю: если разница между weighted и fresh большая — на странице badge «↗ ухудшение» или «↘ улучшение».

### Критерии готовности

- [ ] Юнит-тест `time_decay_weight`: вчера → 1.0, 90 дней → 0.5, год → ~0.06, очень старое → `weight_floor`.
- [ ] При `weighting.enabled=False` пайплайн возвращает только `raw`, остальное `None`. Старые тесты не ломаются.
- [ ] Юнит-тест: датасет из 100 отзывов (50 свежих позитивных, 50 годовалых негативных) → `weighted.negative_share` < `raw.negative_share`.
- [ ] `pain_points[*].avg_age_days` корректен на синтетике.
- [ ] Если `period` короткий (все отзывы за неделю) — веса близки к 1.0, метрики ≈ raw (sanity check).
- [ ] `fresh_kpi` устойчив при отсутствии отзывов в окне (возвращает `None`, а не падает).

### Подсказки

- Хранить веса в БД нет смысла — пересчитываются при каждом запуске от `now`. Это правильно: вчерашний отзыв с весом 0.99 завтра будет 0.98.
- Для воспроизводимости отчётов фиксируй `reference_date` в `PipelineResult.generated_at` и используй его при перерасчётах.
- При смене стратегии или half_life отчёты несравнимы — добавь в footer HTML `weighting: exp, half_life=90, floor=0.05`.

---

## 5. Изменения в существующих задачах

Эти правки нужно применить к ранее созданным `tasks/*.md`:

### `tasks/00_logging.md`
- Удалить sink `obratka.jsonl` — его роль теперь у Phoenix.
- Добавить упоминание `trace_id` в контекст loguru.

### `tasks/01_async_orchestrator.md`
- В начале `run_pipeline` вызывать `setup_phoenix()`.
- Корневой и шаговые спаны через `step_span` (см. раздел 1).
- Передавать `collector: ArtifactCollector | None` во все шаги.
- В конце вызывать `render_report` если `settings.report.enabled`.

### `tasks/02..09` (все шаги)
- Добавить опциональный параметр `collector` в публичные функции.
- В каждом шаге, если `collector is not None`, собирать `StepNArtifact` (см. раздел 3).
- Обернуть тело в `step_span` для Phoenix-трейса.

### `tasks/08_step3_kpi_aggregation.md`
- Применить веса (см. раздел 4).
- Сохранить текущую форму `core_kpi`, `core_kpi_weighted`, `core_kpi_fresh`
  вместо введения `CoreKPIWrapper`.
- `PainPoint` расширить: `negative_share_raw`, `negative_share_weighted`, `weighted_mention_count`, `avg_age_days`, `sample_dates`.

### `tasks/09_step4_recommendations.md`
- В user-prompt передавать `weighted` + `fresh` срезы.
- Дополнить системный промпт инструкцией про сравнение weighted vs fresh.

### `tasks/11_config_and_env.md`
Добавить в `Settings`:

```python
class PhoenixConfig(BaseModel):
    enabled: bool = True
    otlp_endpoint: str = "http://localhost:4318"
    project_name: str = "obratka-dev"
    api_key: str | None = None
    ui_url_template: str = "http://localhost:6006/projects/{project}/traces/{trace_id}"

class ReportConfig(BaseModel):
    enabled: bool = True
    output_dir: str = "reports"
    open_in_browser: bool = False
    max_samples_per_step: int = 20
    chartjs_offline: bool = False

# В PipelineConfig добавить:
weighting: WeightingConfig = WeightingConfig()

# В Settings добавить:
phoenix: PhoenixConfig = PhoenixConfig()
report: ReportConfig = ReportConfig()
```

Соответственно расширить `.env.example`.

### `docs/pricing.md`
- Никак не меняется. Phoenix не добавляет LLM-вызовов, отчёт — алгоритмический, веса — алгоритмические. Стоимость пайплайна та же.

### `tasks/10_database_schema.md`
- Опционально: добавить таблицу `stage_artifacts` (JSONB на каждый run_id), если хочется хранить артефакты в БД, а не только в HTML. По дефолту — не хранить, артефакты живут только в reports/<run_id>.html. Если будет нужна история — поднять флаг и схему доделаем отдельной задачей.

---

## Краткий чек-лист реализации (порядок)

1. `tasks/00_logging.md` — правка (убрать JSONL).
2. **Phoenix setup** — новый модуль `observability/`, `docker-compose.phoenix.yml`, флаг в конфиге.
3. **ArtifactCollector + Pydantic-схемы артефактов** — новый модуль `report/artifacts.py`.
4. Прокинуть `collector` через все шаги (`tasks/02..09`), наполнять артефакты при `not None`.
5. **Time decay** в `step3_kpi.py` — формула, weighted/raw/fresh.
6. **HTML-рендер** — `report/builder.py` + Jinja-шаблон + Chart.js (опц).
7. Интеграция в `orchestrator.py` — `setup_phoenix()` + `render_report()`.
8. Smoke-тест end-to-end на 100 фикстурных отзывов.
