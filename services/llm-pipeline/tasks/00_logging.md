# Задача 00: Логирование (loguru)

## Цель

Единая система логирования, которая работает на всех шагах пайплайна. Логи должны быть полезны для:
- отладки конкретного отзыва (поиск по `review_id`),
- отладки одного запуска пайплайна (поиск по `run_id`),
- мониторинга стоимости (количество токенов, $),
- мониторинга производительности (latency LLM-вызовов).

## Файл

`src/obratka/logging_setup.py`

## Требования

### Что должно быть в каждой записи лога

- `timestamp` — ISO 8601 с миллисекундами
- `level` — `DEBUG | INFO | WARNING | ERROR | CRITICAL`
- `step` — `step0 | step05 | step1 | step2 | step22 | step21 | step3 | step4 | orchestrator`
- `run_id` — UUID запуска пайплайна (генерируется в orchestrator один раз)
- `trace_id` — OpenTelemetry trace id запуска, если Phoenix/OTel активен
- `review_id` — если применимо
- `author_id` — если применимо (для шага 1)
- `batch_id` — если применимо (для шага 2)
- `message` — человекочитаемое сообщение
- сериализуемые `extra` поля (через `logger.bind`)

### Sinks (куда пишем)

1. **Консоль** — цветной формат, level=`INFO` по умолчанию (читается человеком).
2. **`logs/obratka.log`** — полный текстовый формат, level=`DEBUG`, ротация по 50 МБ, хранение 14 дней, сжатие в zip.
3. **`logs/errors.log`** — только level≥`ERROR` со stacktrace, хранение 90 дней.

> v2 / tasks/12: JSONL sink удалён. Его роль выполняют Phoenix/OpenTelemetry
> трейсы. В loguru-контекст пишется `trace_id`, чтобы связать текстовые логи с
> Phoenix UI.

### Контекстные привязки

- В `orchestrator.py` создаётся `run_id = uuid4()` и привязывается к контексту:
  ```python
  with logger.contextualize(run_id=str(run_id)):
      await run_pipeline(...)
  ```
- В каждом шаге локально привязывается `step`:
  ```python
  step_logger = logger.bind(step="step2")
  ```
- При обработке батча привязывается `batch_id`.

### Специальные события (обязательно логировать)

| Событие | Уровень | Поля в `extra` |
|---|---|---|
| Запуск пайплайна | INFO | `business_id`, `total_reviews` |
| Старт шага | INFO | `step`, `input_count` |
| Окончание шага | INFO | `step`, `output_count`, `duration_ms`, `cost_usd` |
| LLM-вызов отправлен | DEBUG | `model`, `prompt_tokens_estimated` |
| LLM-вызов получен | DEBUG | `model`, `prompt_tokens`, `completion_tokens`, `cost_usd`, `latency_ms` |
| Невалидный JSON, retry | WARNING | `attempt`, `error` |
| Превышен rate limit | WARNING | `retry_after_s` |
| Низкий confidence (шаг 2) | INFO | `review_id`, `topic`, `confidence` |
| Отзыв отправлен в low-conf очередь | INFO | `review_id`, `reason` |
| Невалидный JSON после всех ретраев | ERROR | `step`, `model`, `last_error` |
| Кэш-хит | DEBUG | `review_hash`, `cached_at` |
| Завершение пайплайна | INFO | `total_cost_usd`, `total_duration_s` |

### Форматы

**Консоль:**
```
2025-05-06 14:23:11.123 | INFO  | step2     | run=a1b2... batch=07 | Batch processed: 12 reviews, 3 → low-conf queue (0.42s, $0.0008)
```

**Текстовый файл:**
```
2025-05-06T14:23:11.123Z | INFO  | obratka.steps.step2_topics:process_batch:142 | run_id=a1b2... step=step2 batch_id=07 | Batch processed
```

JSONL-формата больше нет: структурная отладка LLM-вызовов ведётся через Phoenix.

## API модуля

```python
from obratka.logging_setup import setup_logging, get_logger

setup_logging(level="INFO", logs_dir="logs")  # вызывается один раз в main
log = get_logger(__name__)

log.bind(step="step2", batch_id="07").info(
    "Batch processed",
    input_count=12,
    low_conf_count=3,
    duration_ms=420,
    cost_usd=0.00081,
)
```

## Зависимости

```toml
loguru = "^0.7"
```

## Критерии готовности

- [ ] `setup_logging()` идемпотентен (повторный вызов не дублирует sinks).
- [ ] Все 4 sink-а пишут одновременно при `log.info(...)`.
- [ ] `run_id` пробрасывается через `contextualize` и виден в каждой записи.
- [ ] Stacktrace ошибок попадает в `errors.log` с полным traceback.
- [ ] Тест: запустить мок-пайплайн на 5 отзывов → найти записи по `run_id` в `obratka.log` и связанный `trace_id` в Phoenix.
- [ ] Чувствительные данные (API-ключи) не логируются — есть фильтр в `setup_logging`.

## Подсказки реализации

- Базовый sink: `logger.remove()` + `logger.add(sys.stderr, ...)` для консоли.
- JSONL — через `serialize=True` или кастомный `format` с `json.dumps`.
- Для фильтра по уровню в файлах — параметр `level=...`.
- Ротация: `rotation="50 MB"`, `retention="14 days"`, `compression="zip"`.
- Цветной вывод в консоль: `colorize=True` + кастомный `format`.
