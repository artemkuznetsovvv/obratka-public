# Задача 08: Шаг 3 — Агрегация KPI (алгоритмический)

## Цель

Свести результаты LLM-шагов в KPI-метрики для бизнеса. Шаг алгоритмический, без LLM. Стоимость = $0.

> v2 / tasks/12: публичный API результата сохраняет текущую совместимую форму:
> `core_kpi`/`loyalty` для raw, `core_kpi_weighted`/`loyalty_weighted` для
> weighted и `core_kpi_fresh`/`loyalty_fresh` для свежего окна. `CoreKPIWrapper`
> не вводится. `aggregate_kpi` принимает `collector` и пишет `Step3Artifact`.

## Файлы

- `src/obratka/steps/step3_kpi.py`

## Зависимости

```toml
pandas = "^2.2"
numpy = "^1.26"
```

## Что считается

### 1. Базовые KPI (5 ключевых)

```python
class CoreKPI(BaseModel):
    avg_rating: float                    # средний рейтинг по звёздам
    rating_dynamics: dict[str, float]    # {"week": +0.1, "month": -0.2, "quarter": +0.05}
    negative_share: float                # доля негативных отзывов (negative + very_negative)
    positive_share: float
    mixed_share: float
    total_reviews: int
    period_start: datetime
    period_end: datetime
```

### 2. Индекс лояльности (NPS-подобный)

```python
class LoyaltyIndex(BaseModel):
    score: float          # от -100 до +100
    promoters_pct: float  # доля "очень позитивных"
    passives_pct: float   # нейтральных + смешанных
    detractors_pct: float # негативных + очень негативных
```

Формула: `score = promoters_pct - detractors_pct` (масштабируется к -100..+100).

Маппинг тональностей:
- `очень позитивный`, `позитивный` → promoter
- `нейтральный`, `смешанный` → passive
- `негативный`, `очень негативный` → detractor

### 3. Болевые точки

```python
class PainPoint(BaseModel):
    topic: str
    negative_share: float          # доля негатива по этой теме
    mention_count: int             # сколько раз упоминалась
    growth_pct: float | None       # рост/падение негатива vs прошлый период
    sample_fragments: list[str]    # 3 примера цитат
    is_low_confidence_dominant: bool   # >50% упоминаний с low_confidence_final?
```

Критерий «болевая точка»:
- `negative_share > 0.30` ИЛИ
- `negative_mention_count >= 3` ИЛИ
- `weighted_negative_mention_count >= 2.0` ИЛИ
- `growth_pct > 50%` (резкий рост негатива)
- И `mention_count >= 5` (отсекаем шум).

Важно: доля негатива не должна быть единственным критерием. В темах с большим
объёмом позитива несколько важных негативных отзывов могут раствориться в
знаменателе, хотя бизнесу всё равно нужно их видеть.

### 4. Позитивные черты

```python
class PositivePoint(BaseModel):
    topic: str
    positive_share: float
    positive_share_weighted: float | None
    positive_mention_count: int
    weighted_positive_mention_count: float | None
    mention_count: int
    weighted_mention_count: float | None
    sample_fragments: list[str]
    sample_dates: list[datetime]
    avg_age_days: float | None
    is_low_confidence_dominant: bool
```

Критерий «позитивная черта»:
- `positive_share_weighted > 0.50` ИЛИ
- `positive_mention_count >= 5`
- И `mention_count >= 5`.

Позитивные черты используются в HTML-отчёте и в Шаге 4, чтобы рекомендации
не только исправляли проблемы, но и сохраняли сильные стороны бизнеса.

### 5. Динамика по периодам

```python
class TrendData(BaseModel):
    period: str                    # "week" | "month" | "quarter"
    bins: list[TrendBin]

class TrendBin(BaseModel):
    start: datetime
    end: datetime
    avg_rating: float
    review_count: int
    negative_share: float
```

### 6. Доля фейков

```python
class FakeStats(BaseModel):
    total_collected: int           # всего отзывов до фильтрации
    fakes_detected: int
    fakes_share: float             # fakes_detected / total_collected
    suspicious_authors: int
```

### Финальный отчёт

```python
class PipelineResult(BaseModel):
    run_id: str
    business_id: int
    generated_at: datetime
    period_start: datetime
    period_end: datetime
    core_kpi: CoreKPI
    loyalty: LoyaltyIndex
    pain_points: list[PainPoint]
    positive_points: list[PositivePoint]
    trends: dict[str, TrendData]   # {"week": ..., "month": ..., "quarter": ...}
    fake_stats: FakeStats
    low_confidence_count: int      # сколько отзывов даже после Шага 2.2 имеют low_confidence_final
    recommendations: list[Recommendation]   # заполняется на Шаге 4
    total_cost_usd: float
```

## API

```python
def aggregate_kpi(
    analyses: list[ReviewAnalysis],
    fake_verdicts: dict[str, AuthorVerdict],
    raw_reviews: list[NormalizedReview],
    business_id: int,
) -> PipelineResult: ...
```

## Логирование

- INFO итог: `avg_rating`, `negative_share`, `pain_points_count`, `nps_score`, `fakes_share`, `low_confidence_count`.

## Критерии готовности

- [ ] Юнит-тесты на каждый KPI с известными входными данными.
- [ ] Динамика корректно считается даже при отсутствии данных за прошлый период (возвращает `None`).
- [ ] Если все отзывы fake — отчёт корректно отрабатывает с пустыми `pain_points` и понятными метриками.
- [ ] Отзывы с `low_confidence_final=True` учитываются с пометкой в `pain_points.is_low_confidence_dominant`.

## Подсказки

- Используй pandas для группировок и оконных функций (rolling по периодам).
- Проверка временных интервалов: используй `pd.Grouper(freq='W')`, `'M'`, `'Q'`.
- `sample_fragments` — берём по 3 наиболее «уверенных» цитаты (по confidence) из аспектов с негативной тональностью.
