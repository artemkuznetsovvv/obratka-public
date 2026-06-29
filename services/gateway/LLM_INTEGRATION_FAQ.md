# LLM Integration FAQ

> Дополнение к **`LLM_PYTHON_QUICKSTART.md`** — там полный контракт на schema_version `2.0`
> и Python skeleton. Этот документ покрывает темы, которые в QUICKSTART не помещаются:
> credentials prod-стенда, edge cases, schema versioning, ручное восстановление,
> ответы на типовые вопросы.
>
> Контракт зафиксирован в `ADR-004`. Зеркальный документ от LLM-команды:
> `llm_pipline/llm_contracts_changed.md`.

---

## 1. Где что описано

| Тема | Документ | Раздел |
|---|---|---|
| Контракт RabbitMQ + S3 | `LLM_PYTHON_QUICKSTART.md` | §«Контракт коммуникации» |
| Схема `output_reviews.json` | `LLM_PYTHON_QUICKSTART.md` | §3.1 |
| Схема `output_summary.json` | `LLM_PYTHON_QUICKSTART.md` | §3.2 |
| Python skeleton | `LLM_PYTHON_QUICKSTART.md` | §«Python skeleton» |
| REST status endpoint | `LLM_PYTHON_QUICKSTART.md` | §5 |
| Credentials prod | этот документ | §3 |
| Schema versioning | этот документ | §4 |
| Failure semantics | этот документ | §5 |
| FAQ (типовые вопросы) | этот документ | §6 |

---

## 2. Pipeline overview

```
                     [PG]                                       [LLM service]
   StartAnalysis  ────►
                       │
                       │ собирает отзывы из 2GIS/Yandex/Google
                       │ → пишет в S3: {jobId}/raw/{source}.json
                       │ → ингестит → input.json в S3
                       │
                       ├────► publish LlmRequestMessage ────►   consume
                       │      (queue: llm.requests,             read input.json
                       │       MassTransit envelope)            inference
                       │                                        write output_reviews.json
                       │                                        write output_summary.json
                       │      consume                ◄──── publish LlmResultMessage
                       │      (queue: llm.results,              (raw JSON, без envelope)
                       │       UseRawJsonDeserializer)
                       │
                       │ читает оба output-файла → save в БД
                       │ → публикует AnalysisCompletedEvent
                       ▼
                       (Web API строит дашборд)

                    REST status:  PG ──GET /status/{jobId}──► LLM
                    (по запросу UI / для будущего reconciliation)
```

---

## 3. Доступы (prod, после деплоя)

### 3.1. Деплой LLM-сервиса

LLM-сервис разворачивается как **отдельный docker-контейнер** в той же docker-network, что
parser-стек (`parser-service_internal`). Тогда внутри сети сервисы видят друг друга по DNS:

| Компонент | DNS-имя | Порт |
|---|---|---|
| RabbitMQ | `rabbitmq` | `5672` |
| MinIO S3 | `minio` | `9000` |
| Seq (опционально, для логов) | `seq` | `80` (UI) / `5341` (ingestion) |
| LLM service (себя) | `llm-pipeline` | `8000` (REST) |

ENV-переменные LLM-контейнера (мы их подскажем):

```
RABBIT_URL=amqp://${RABBIT_USER}:${RABBIT_PASS}@rabbitmq:5672/
S3_ENDPOINT=http://minio:9000
S3_ACCESS_KEY=${MINIO_ACCESS_KEY}
S3_SECRET_KEY=${MINIO_SECRET_KEY}
S3_BUCKET=obratka-jobs
LLM_HTTP_PORT=8000
SEQ_SERVER_URL=http://seq:80          # опционально
SEQ_INGESTION_KEY=...                 # опционально
```

`${RABBIT_USER}` / `${RABBIT_PASS}` / `${MINIO_ACCESS_KEY}` / `${MINIO_SECRET_KEY}` —
существующие ключи из `~/parser-service/.env` (на MVP LLM ходит под общим юзером).

### 3.2. Если LLM на отдельном хосте (не в нашей сети)

Это сложнее: RabbitMQ и MinIO у нас наружу не выставлены (только через nginx-vhost-ы для UI:
`logs.193.233.217.223.sslip.io` для Seq, `s3-admin.193.233.217.223.sslip.io` для MinIO console).
Для прямого доступа к AMQP `5672` или S3 API `9000` нужно либо:

1. Открыть порты на VPS-firewall + IP allowlist в nginx-stream (для AMQP) — обсудить отдельно.
2. SSH-tunnel на dev-этапе.
3. Запустить LLM на том же VPS как контейнер — рекомендуемый путь (см. §3.1).

### 3.3. Права в RabbitMQ (на проде, future)

На MVP LLM ходит под общим `${RABBIT_USER}`. На проде выделим юзера `llm` с минимальными правами:

- `Read` на queue `llm.requests`
- `Write` на queue `llm.results`
- `Configure` нет

### 3.4. Права в MinIO (на проде, future)

На MVP — root credentials. На проде — IAM-user `llm` с policy:

- `s3:GetObject` на `arn:aws:s3:::obratka-jobs/*/input.json`
- `s3:PutObject` на `arn:aws:s3:::obratka-jobs/*/output_reviews.json`
- `s3:PutObject` на `arn:aws:s3:::obratka-jobs/*/output_summary.json`
- ❌ нет `ListBucket`, нет write на `input.json`/`raw/*`

Principle of least privilege.

---

## 4. Schema versioning

`schema_version` присутствует в:
- `LlmRequestMessage` (что мы публикуем)
- `LlmResultMessage` (что вы публикуете)
- `input.json` (мы пишем в S3)
- `output_reviews.json` + `output_summary.json` (вы пишете в S3)

**Текущая версия:** `"2.0"`.

| Действие | Меняет ли major? | Координация |
|---|---|---|
| Новые поля в `output_reviews.json` или `output_summary.json` | Нет | Не нужна (PG-сторона tolerant deserializer) |
| Новые поля в `input.json` | Нет | LLM игнорирует unknown |
| Удаление required-поля или смена типа | Да (`3.0`) | Обязательная синхронизация обеих сторон |
| Получение `schema_version` старше ожидаемого | warning в логах | продолжаем |
| Major mismatch (ждём `2.x`, пришло `3.x`) | LLM возвращает `failed`, PG ставит job в `failed` | согласованный апгрейд |

ENV-переменная PG: `Llm__SchemaVersion = "2.0"`. Когда апгрейдимся — задаём через ENV
без передеплоя кода.

---

## 5. Failure semantics

### 5.1. Что делает PG при сбое

| Сценарий | Действие PG |
|---|---|
| Брокер недоступен на publish `LlmRequestMessage` | MassTransit retry с exponential backoff; при исчерпании → `analysis_jobs.status='failed'` |
| `output_reviews.json` 404 при попытке прочитать | job в `failed`, error="output_reviews.json not found at <url>" |
| `output_summary.json` 404 | job в `failed` |
| Битый JSON в одном из output-файлов | job в `failed`, error содержит десериализационную ошибку |
| `analysis_job_id` в output не совпадает с request | job в `failed`, error="LLM output mismatch: expected X, got Y" |
| `recommendations_count != len(full_recommendations)` | warning в логах, продолжаем (берём фактическое значение `len`) |
| Ответ LLM не пришёл за `Llm__ResultTimeoutMinutes` (30 мин default) | сейчас → ручной recovery через `/api/qa/llm/replay/{jobId}`. Future → REST-fallback к `http://llm-pipeline:8000/status/{jobId}` |

### 5.2. Идемпотентность LLM-стороны

PG может доставить один и тот же `LlmRequestMessage` несколько раз (RabbitMQ at-least-once
+ MassTransit redelivery при transient ошибках). LLM-сервис должен:

- **Либо** проверять «уже обрабатывал ли я этот `analysis_job_id`» (своё state) и в дубле
  отвечать `finished` с тем же URL'ами.
- **Либо** перепрогонять обработку — это безопасно, потому что:
  - `output_reviews.json` / `output_summary.json` перезаписываются (S3 PUT идемпотентен)
  - PG-ингест в БД через `ON CONFLICT DO NOTHING` (review_llm_results) и
    `DELETE WHERE job_id=X + bulk INSERT` (analysis_recommendations)

Второй путь проще, но дороже по compute. Текущая Python-имплементация выбрала второй.

### 5.3. Идемпотентность PG-стороны

PG может получить твой `LlmResultMessage` несколько раз. Поведение:

- Job уже в `computing_aggregates` или дальше → второй приход молча игнорируется.
- Job в `sent_to_llm` → ингест проходит как обычно.
- В БД дубли строк не возникают: UNIQUE-constraint на `review_llm_results(review_id, analysis_job_id)`,
  `analysis_recommendations` пересоздаётся через DELETE+INSERT.

«Ровно один раз» с твоей стороны не нужен. Гарантируй at-least-once, дубли мы поглотим.

---

## 6. FAQ (вопросы и edge cases)

### 6.1. Почему `LlmResultMessage` — raw JSON, а `LlmRequestMessage` — MassTransit envelope?

PG публикует через MassTransit (это родной .NET-код), envelope автоматический. LLM-сервис
на Python — публиковать MassTransit envelope с правильными `messageType` URN неудобно,
поэтому LLM публикует raw JSON, а PG-consumer для `llm.results` настроен на
`UseRawJsonDeserializer()`. Внутри RabbitMQ всё так же — `delivery_mode: PERSISTENT`,
`durable: true`.

### 6.2. `review_id` — число или строка?

**Сохраняется тип из input.json.** Сейчас в `input.json` PG кладёт `bigint` (наш `reviews.id`
в БД — `bigint`, не UUID — отклонение от ADR-002 в пользу компактности high-volume таблицы).
LLM возвращает то же значение и тот же тип. Если когда-нибудь PG переедет на UUID — LLM-сервис
не сломается, потому что просто пробрасывает тип.

### 6.3. Что если в `output_reviews.json` есть `review_id`, которого не было в input?

Мы сохраним строку в `review_llm_results`, но без записи в `analysis_job_reviews` она не
свяжется с job-ом → не отобразится на дашборде. Не критично, но лучше не делать.

### 6.4. Можно ли вернуть только часть отзывов (не все из input)?

Да, как edge case: LLM может пометить какие-то отзывы как невозможные для классификации.
Тогда соответствующего объекта в `reviews[]` просто не будет. Но **сейчас** Python-имплементация
гарантирует «один объект на каждый input review» (для отзыва без сигналов — `aspects:[]`,
`overall_sentiment:"нейтральный"`, `overall_confidence:0.0`). Если будете отклоняться —
сообщите.

### 6.5. Один и тот же `topic` в нескольких aspects одного review

**Допускается**. Например негативный комментарий про персонал в целом + позитивный про
конкретного администратора. PG агрегирует:
- При построении дашборда «упоминания темы X» — каждый aspect считается отдельно.
- Sentiment-распределение по теме строится по `aspects[].sentiment`, а не `overall_sentiment`.

### 6.6. `aspects[].fragment` — пустая строка?

**Допускается**. Модель может извлечь тему без однозначной цитаты. PG не делает строгой
валидации этого поля, просто хранит как есть.

### 6.7. Лимит на размер `input.json`?

На MVP — до 1000 отзывов на job (~500 КБ JSON). Целевой объём LLM_pipeline — до 15 000
отзывов на цикл анализа (см. `llm_pipline/CLAUDE.md`). При больших объёмах обсуждаем
чанкинг, batching, частичные ответы.

### 6.8. Лимит на время обработки?

`Llm__ResultTimeoutMinutes = 30` (default). На объёме ~250 отзывов LLM-команда показала ~104 секунды,
так что 30 мин — большой запас. Если для крупных job-ов надо больше — поднимем ENV без
передеплоя кода.

### 6.9. Если RabbitMQ упал — куда деваются уже отправленные сообщения?

Persistent message + durable queue (наш default) → переживут рестарт брокера. Если потерян
(transient) → восстановление через `/api/qa/llm/replay/{jobId}` (PG переотправит request).

### 6.10. Можно использовать другой broker (Kafka, NATS)?

ADR-004 фиксирует RabbitMQ. Архитектурное изменение — обсуждается отдельно.

### 6.11. Где взять пример реального `input.json`?

Запусти QA-ручку PG (см. `test-cases.md` §2) на dev-VPS — Parser соберёт отзывы, PG напишет
input.json, в MinIO Console (`https://s3-admin.193.233.217.223.sslip.io`) можешь скачать
и использовать как fixture для тестирования.

Альтернативно — попроси нас, выдадим sample.

### 6.12. Что если LLM падает в середине обработки 1000 отзывов?

LLM публикует `LlmResultMessage(status=failed)` с текстом ошибки. PG ставит job в `failed`
и шлёт alert в Telegram. После починки — manual replay через `/api/qa/llm/replay/{jobId}`,
LLM получает request заново, переобрабатывает с нуля.

Атомарность по отдельным reviews (например «обработали 700, упали на 701, доработать
оставшиеся 300») на MVP **не поддерживается** — это привнесёт сложность в идемпотентности.

### 6.13. Можно ли использовать другую модель / поменять промпт?

Да, это **внутреннее дело LLM-сервиса**. Контракт с PG — только формат input/output. Внутри
LLM-pipeline'а есть multi-step (см. `llm_pipline/CLAUDE.md`): нормализация, перевод, детекция
фейков, sentiment+темы, переклассификация low-confidence, кластеризация, KPI, рекомендации.
Каждый шаг может использовать свою модель — нам не важно.

---

## 7. Чек-лист «готов к интеграции»

Перед подключением реального LLM-сервиса в prod-стенд:

- [ ] Локально: LLM-worker подписан на `llm.requests`, читает `input.json`, кладёт оба
      output-файла, публикует `LlmResultMessage(finished)`. PG переходит в `computing_aggregates`.
- [ ] Локально: проверил failure path — публикуешь `failed` с непустым `error`. PG переходит в `failed`.
- [ ] Локально: проверил **дубль** request с тем же `analysis_job_id` — твой сервис не двоит
      обработку (или двоит, но output идентичен).
- [ ] REST endpoint `/status/{job_id}` отдаёт корректный stage/progress. `/health/live` отвечает.
- [ ] PG-сторона: `LlmResultMessage` consumer переключён на `UseRawJsonDeserializer()`.
- [ ] PG-сторона: миграция БД применена (новый `review_llm_results.aspects`, `analysis_jobs.summary`,
      таблица `analysis_recommendations`).
- [ ] Согласован `schema_version` (на старте `"2.0"`).
- [ ] Согласованы конкретные значения ENV для LLM-контейнера (RABBIT_URL и т.д.).
- [ ] LLM-контейнер добавлен в `~/processing-gateway/docker-compose.yml` (вместо `llm-stub`).

После — запускаем тестовый job через `/api/qa/analyses` на dev-VPS, смотрим Seq и MinIO console.
