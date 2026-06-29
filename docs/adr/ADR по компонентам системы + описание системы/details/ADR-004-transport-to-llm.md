# ADR-004: Транспорт между системой и внешним LLM-сервисом

## Context

Система взаимодействует с внешним LLM-сервисом как с чёрным ящиком.
Наша ответственность: отправить отзывы на обработку и корректно принять результат.

**Контракт с LLM-сервисом (зафиксировано):**
- Отправка данных и получение результатов → через брокер сообщений
- Проверка статуса задачи → REST-методы LLM-сервиса (`processing` / `finished` / `failed`)
- Брокер находится на нашей стороне (мы администрируем)

**Ограничения MVP:**
- Максимум **1000 отзывов на компанию** за один анализ
- Хранение входных данных (отзывы) и выходных данных (результаты LLM) — бессрочно

**Допущения (зафиксированы явно):**
- Лимиты LLM-сервиса на размер принимаемых данных неизвестны.
  Допущение: сервис принимает любой объём в одном сообщении.
  При обнаружении лимита — решение уже готово (claim-check, см. ниже).

## Decision

### 1. Брокер: MassTransit + RabbitMQ

**RabbitMQ** — оптимален для task queue паттерна (наш случай: одна задача → один consumer → результат):
- Нативная поддержка work queues, acknowledgment, dead-letter
- Операционно проще Kafka при MVP-нагрузке (единицы одновременных задач)
- Нет нужды в event replay, log compaction, партиционировании — всё это Kafka-фичи, которые нам не нужны

**MassTransit** как абстракция:
- Скрывает детали RabbitMQ; смена на Kafka при росте нагрузки = изменение конфига
- Built-in: retry, exponential backoff, dead-letter queue, correlation

### 2. Передача данных: Claim-check через blob-хранилище (MinIO)

Брокер используется **только для сигналов** (job_id + ссылка на данные).
Сами данные (отзывы и результаты) хранятся в blob-хранилище.

**Мотивация:**
- 1000 отзывов сейчас — ~300 КБ (умещается в сообщение), но при росте числа филиалов объём легко превысит 10 000 отзывов
- Claim-check снимает вопрос размера раз и навсегда
- Даёт бесплатный аудит-лог: и входные данные (что отправили в LLM), и результаты хранятся в S3
- MinIO — S3-совместимое self-hosted хранилище; в продакшн заменяется на AWS S3 / Yandex Object Storage без изменения кода

**Структура хранилища:**
```
s3://obratka-jobs/
  {analysis_job_id}/
    input.json    ← наши данные (мы пишем, LLM читает)
    output.json   ← результат LLM (LLM пишет, мы читаем)
```

**Содержимое input.json:**
```json
{
  "schema_version": "1.0",
  "analysis_job_id": "uuid",
  "company_id": "uuid",
  "reviews": [
    {
      "review_id": "uuid",
      "text": "Отличный сервис, рекомендую",
      "source": "2gis",
      "date": "2024-03-15T14:22:00Z",
      "stars": 5,
      "branch_id": "uuid"
    }
  ]
}
```

**Содержимое output.json (структура от LLM):**
```json
{
  "schema_version": "1.0",
  "analysis_job_id": "uuid",
  "recommendation": "Улучшить скорость обслуживания, уделить внимание качеству еды...",
  "processedReview": [
    {
      "review_id": "uuid",
      "fake_status": "normal",
      "fake_reason_tags": [],
      "sentiment": "positive",
      "sentiment_confidence": 0.92,
      "is_spam": false,
      "spam_confidence": 0.04,
      "topics": ["персонал", "качество еды"]
    }
  ]
}
```

**Важно:** `recommendation` — строка на уровне job, не per review.
Processing Gateway сохраняет per-review результаты из `processedReview` в `review_llm_results`,
а `recommendation` — в `analysis_jobs.recommendation`.

### 3. Схема сообщений в брокере

**Сообщение нашей системы → LLM (публикуем в очередь):**
```json
{
  "analysis_job_id": "uuid",
  "company_id": "uuid",
  "payload_url": "s3://obratka-jobs/{job_id}/input.json",
  "review_count": 847,
  "schema_version": "1.0",
  "callback_queue": "llm.results"
}
```

**Сообщение LLM → нашей системы (LLM публикует в нашу очередь):**
```json
{
  "analysis_job_id": "uuid",
  "status": "finished",
  "result_url": "s3://obratka-jobs/{job_id}/output.json",
  "schema_version": "1.0"
}
```

При `status: "failed"` поле `result_url` отсутствует, добавляется `"error": "..."`.

### 4. Внутреннее отслеживание прогресса (для progress screen)

Processing Gateway ведёт таблицу `analysis_jobs` в БД:

| Поле | Тип | Описание |
|------|-----|----------|
| `id` | uuid | Идентификатор задачи |
| `status` | enum | `pending` → `collecting` → `language_detection` → `sent_to_llm` → `computing_aggregates` → `completed` / `partial` / `failed` |
| `company_id` | uuid | |
| `review_count` | int | Количество собранных отзывов (обновляется после сбора) |
| `collection_progress` | JSONB | `{"2gis": {"status": "running", "progress": 60}, "yandex": {"status": "completed", "review_count": 143}}` — статус per source во время сбора |
| `payload_url` | text | Ссылка на input.json в S3 |
| `result_url` | text | Ссылка на output.json в S3 (заполняется по завершении) |
| `recommendation` | text | Текст рекомендации от LLM (job-level, извлекается из output.json) |
| `created_at` | timestamp | |
| `sent_at` | timestamp | Когда опубликовали в брокер LLM |
| `completed_at` | timestamp | |
| `error` | text | Причина сбоя |

**Переходы статусов и их смысл:**

| Статус | Когда устанавливается | Пользователь видит |
|--------|----------------------|-------------------|
| `pending` | Команда получена от Web API | — |
| `collecting` | Первый POST /collection-tasks отправлен в Parser | «Сбор отзывов» |
| `language_detection` | Все Parser-задачи завершены; PG определяет язык отзывов | «Определение языка» |
| `sent_to_llm` | input.json загружен в S3, сообщение опубликовано в брокер | «Анализ (фейки, тональность, темы)» |
| `computing_aggregates` | LLM вернул результат; Analytics-модуль считает агрегаты | «Построение дашборда» |
| `completed` | Агрегаты готовы, дашборд доступен | «Готово» |
| `partial` | Часть источников недоступна, но анализ выполнен по остальным | «Готово (частично)» |
| `failed` | Критическая ошибка | «Ошибка» |

**Progress screen на фронте** → polling Web API каждые 3–5 сек:
```
GET /api/analyses/{job_id}/status
→ {
    "status": "collecting",
    "stages": [
      { "key": "collecting",         "label": "Сбор отзывов",                       "state": "active" },
      { "key": "language_detection", "label": "Определение языка",                  "state": "pending" },
      { "key": "llm_analysis",       "label": "Анализ (фейки, тональность, темы)",  "state": "pending" },
      { "key": "building_dashboard", "label": "Построение дашборда",                "state": "pending" }
    ],
    "sources": {
      "2gis":   { "status": "running",   "progress": 60 },
      "yandex": { "status": "completed", "review_count": 143 }
    },
    "review_count": 143
  }
```

`state` значения: `pending` | `active` | `completed` | `failed`

Стадии `collecting.sources` отражают `collection_progress` из таблицы `analysis_jobs`;
обновляются при каждом ответе Parser-поллинга.

REST-методы LLM-сервиса (`/status/{job_id}`) используются как **reconciliation fallback**: если broker-ответ не пришёл в течение настраиваемого таймаута → Processing Gateway поллит LLM-статус напрямую.

### 5. Обработка сбоев

| Сценарий | Поведение |
|----------|-----------|
| S3 недоступен при загрузке input | Retry с backoff; при исчерпании → `failed` + алерт |
| LLM не взял сообщение из очереди | RabbitMQ держит сообщение; статус остаётся `sent_to_llm` |
| LLM вернул `failed` | Обновляем статус; уведомляем Notifications-модуль (Web API) |
| Ответ от LLM не пришёл за таймаут | Reconciliation через REST-статус LLM |
| Reconciliation тоже `failed` | Dead-letter queue + Telegram-алерт администратору |
| Брокер недоступен | MassTransit retry с exponential backoff |

## Consequences

**Плюсы:**
- Брокер — шина сигналов, не транспорт данных: сообщения всегда маленькие (< 1 КБ)
- Claim-check масштабируется: 1000 или 100 000 отзывов — схема не меняется
- Аудит-лог бесплатно: input.json и output.json хранятся в S3
- Смена blob-провайдера (MinIO → AWS S3) = изменение конфига клиента
- MassTransit абстрагирует брокер: смена RabbitMQ → Kafka без переписывания кода

**Минусы / риски:**
- Добавляется MinIO в инфраструктуру (ещё один компонент для операционного обслуживания)
- Нужно согласовать с LLM-сервисом: доступ к нашему S3 для чтения input.json и записи output.json (IAM policy / presigned URLs — решить отдельно)
- Polling прогресса каждые 3–5 сек создаёт нагрузку на Web API (при MVP-масштабе управляемо)

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| ~~Как LLM-сервис получает доступ к S3~~ | ✅ Решено: LLM-сервис получает постоянные IAM credentials с правами read на `input.json` и write на `output.json` в bucket `obratka-jobs`. Принцип least privilege: только этот bucket, только эти операции. |
| Версионирование схемы сообщений при изменении контракта | До первого деплоя |
| Аутентификация LLM-сервиса в нашем RabbitMQ | До интеграции с LLM |
| Подтвердить допущение о лимитах LLM на размер данных | При первом контакте с командой LLM |
| Политика retention для S3 (сейчас бессрочно, пересмотреть после MVP) | После MVP |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Kafka вместо RabbitMQ | Task queue паттерн — не стриминг; оverkill при MVP-нагрузке; сложнее в ops |
| Embed payload в брокер-сообщение | Не масштабируется при росте отзывов (>1000/компания при множестве филиалов) |
| WebSocket / SSE для прогресса | Усложняет инфраструктуру; polling достаточен при MVP-нагрузке |
| MassTransit in-memory | Не подходит: LLM — внешний сервис за пределами нашего процесса |
| Передача через REST к LLM | LLM-сервис определил async-контракт через брокер |
