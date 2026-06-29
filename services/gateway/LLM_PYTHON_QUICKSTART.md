# LLM service — quickstart для Python-разработчика

Standalone Python worker, общается с Processing Gateway (PG) через **RabbitMQ** + **S3**.
Дополнительно — отдаёт **REST status-endpoint** для отслеживания прогресса.

---

## TL;DR

```
RabbitMQ llm.requests   →   download input.json (S3)
                        →   LLM inference
                        →   upload output_reviews.json + output_summary.json (S3)
                        →   publish to RabbitMQ llm.results

REST GET /status/{job_id}  ←  PG polls для отображения прогресса
```

PG не общается с LLM напрямую через бизнес-логику — только broker + claim-check (S3).
REST используется только для опроса статуса.

---

## Connection details

LLM-сервис разворачивается в **той же docker-network**, что PG (`parser-service_internal`).
Внутри сети сервисы видят друг друга по DNS-именам:

| Что | Host:Port | Источник credentials |
|---|---|---|
| RabbitMQ AMQP | `rabbitmq:5672` | `RABBIT_USER` / `RABBIT_PASS` (из admin-а PG) |
| MinIO S3 | `minio:9000` | `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` |
| MinIO bucket | `obratka-jobs` | общий с PG и Parser |

ENV-переменные для контейнера:

```bash
RABBIT_URL=amqp://<RABBIT_USER>:<RABBIT_PASS>@rabbitmq:5672/
S3_ENDPOINT=http://minio:9000
S3_ACCESS_KEY=<MINIO_ACCESS_KEY>
S3_SECRET_KEY=<MINIO_SECRET_KEY>
S3_BUCKET=obratka-jobs
LLM_HTTP_PORT=8000
```

Для **локальной разработки** на своей машине — можно поднять PG-стек локально через
его `docker-compose.yml`, тогда `rabbitmq` / `minio` доступны как `localhost:5672` / `localhost:9000`
с дефолтными creds (`gateway/gateway_pwd`, `minioadmin/minioadmin`).

---

## Контракт коммуникации

### 1. Inbound: топик `llm.requests`

Сообщение приходит обёрнутым в **MassTransit envelope** (PG публикует через MassTransit).
Внешний JSON содержит метаполя; **реальный payload — в поле `message`**:

```json
{
  "messageId": "...",
  "messageType": ["urn:message:..."],
  "headers": { ... },
  "message": {
    "analysis_job_id": "uuid",
    "company_id":      "uuid",
    "payload_url":     "s3://obratka-jobs/<jobId>/input.json",
    "review_count":    847,
    "schema_version":  "2.0",
    "callback_queue":  "llm.results"
  }
}
```

В коде распаковка: `payload = body.get("message", body)` (защита если когда-то перейдём на raw JSON).

Поля:
- `payload_url` — путь к input в S3
- `callback_queue` — куда публиковать ответ (всегда `llm.results` на MVP)
- `schema_version` — версия контракта (`2.0` = два output-файла)

### 2. S3 input: `s3://obratka-jobs/<jobId>/input.json`

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "uuid",
  "company_id": "uuid",
  "business_category": "Общественное питание",
  "business_subcategory": "Бар",
  "additional_context": "Супер крутое заведение! Хочу узнать больше про отзывы о блюдах.",
  "reviews": [
    {
      "review_id": "uuid-or-string",
      "text": "потрясающе атмосферное место с таким кофе...",
      "source": "yandex",
      "date": "2024-03-10T09:00:00Z",
      "stars": 5,
      "branch_id": "uuid"
    }
  ]
}
```

`review_id` **обязательно** возвращать в output **в исходном типе** (если в input был `int64` — на выходе тоже число, не строка). Иначе PG не свяжет результат с записью в БД.

**`business_category` / `business_subcategory` / `additional_context`** — опциональный бизнес-контекст
компании из формы нового анализа (категория/подкатегория бизнеса + свободный текст пользователя).
Поля **аддитивные**: `schema_version` остаётся `2.0` (политика §6 — новые поля версию не бампают).
Каждое поле может быть `null` (запуск без контекста). LLM-сервис волен **использовать их как
доп. контекст для промптов или игнорировать** — обязательными они не являются.

### 3. S3 outputs (LLM пишет два файла)

#### 3.1. Per-review: `s3://obratka-jobs/<jobId>/output_reviews.json`

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "uuid",
  "reviews": [
    {
      "review_id": "2-qhHbn75aXvBV9mmjoj6j6ANv5sRRvuM",
      "text": "потрясающе атмосферное место с таким кофе, ради которого стоит специально зайти и попробовать. я пил конкретно флэтуайт, и меня просто поразило, насколько было вкусно! помещение маленькое, ассортимент небольшой - но в этом жанре это вообще не недостатки.",
      "overall_sentiment": "позитивный",
      "overall_confidence": 0.9,
      "aspects": [
        {
          "topic": "атмосфера",
          "sentiment": "позитивный",
          "confidence": 0.9,
          "fragment": "потрясающе атмосферное место",
          "is_freeform": false
        },
        {
          "topic": "еда/напитки",
          "sentiment": "позитивный",
          "confidence": 0.9,
          "fragment": "кофе, ради которого стоит специально зайти и попробовать",
          "is_freeform": false
        }
      ]
    }
  ]
}
```

Поля aspect-объекта:

| Поле | Тип | Описание |
|---|---|---|
| `topic` | string | `атмосфера`, `еда/напитки`, `сервис`, `чистота`, `цена`, ... или произвольная если `is_freeform: true` |
| `sentiment` | string | `позитивный` / `негативный` / `нейтральный` |
| `confidence` | float | 0.0–1.0 |
| `fragment` | string | Цитата из `text`, к которой относится aspect |
| `is_freeform` | boolean | `true` если `topic` не из закрытого списка |

#### 3.2. Summary: `s3://obratka-jobs/<jobId>/output_summary.json`

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "uuid",
  "recommendations_count": 8,
  "summary": "На основе анализа отзывов и KPI, ключевые проблемы связаны с персоналом, системой записи и прозрачностью цен. Рекомендуется внедрить долгосрочные изменения в обучении персонала и системе записи.",
  "full_recommendations": [
    {
      "priority": 1,
      "topic": "персонал",
      "title": "Улучшение коммуникации и обучения персонала",
      "body": "На основе отзывов, персонал часто не уделяет достаточного внимания клиентам и плохо объясняет информацию. Рекомендуется внедрить регулярные тренинги по коммуникации.",
      "expected_impact": "Улучшение восприятия клиентами качества обслуживания и уменьшение жалоб на персонал.",
      "evidence": [
        "врач максимально не хотел уделять внимание, даже направления были написаны плохо",
        "девушки на регистратуре общаются с клиентами так, что приходится из них информацию всю выуживать"
      ]
    }
  ]
}
```

Поля верхнего уровня:

| Поле | Тип | Описание |
|---|---|---|
| `schema_version` | string | `"2.0"` |
| `analysis_job_id` | uuid | тот же что в input.json |
| `recommendations_count` | int | равен `len(full_recommendations)` (PG проверяет как инвариант) |
| `summary` | string | краткое резюме на 1–3 предложения. Для пустого input — fallback вида «Недостаточно данных…» |
| `full_recommendations` | array | отсортирован по `priority` ASC, при равенстве — больше `evidence` сверху |

Поля одной рекомендации:

| Поле | Тип | Описание |
|---|---|---|
| `priority` | int | `1` — критично (безопасность, репутация, повторные визиты), `2` — важно (UX, конверсия), `3` — полезно (маркетинг) |
| `topic` | string | тема (например `персонал`, `атмосфера`, `качество лечения`) |
| `title` | string | короткий заголовок рекомендации |
| `body` | string | что именно делать |
| `expected_impact` | string | ожидаемый эффект для бизнеса |
| `evidence` | string[] | цитаты из отзывов или ссылки на `review_id` (может быть `[]`, но не отсутствовать) |

> **Изменения относительно ранней схемы**: больше **нет** полей `recommendation` (одна строка) и
> `summary_stats { total_reviews, sentiment_distribution, top_topics }`. Для PDF-отчёта используется
> `summary` + цикл по `full_recommendations[]`. Если PG нужны агрегатные числа (sentiment-распределение,
> top topics) — они считаются на стороне PG из `output_reviews.json`.

### 4. Outbound: топик `llm.results`

После успешной загрузки обоих файлов:

```json
{
  "analysis_job_id":      "uuid",
  "status":               "finished",
  "result_reviews_url":   "s3://obratka-jobs/<jobId>/output_reviews.json",
  "result_summary_url":   "s3://obratka-jobs/<jobId>/output_summary.json",
  "schema_version":       "2.0"
}
```

При неудаче:

```json
{
  "analysis_job_id":  "uuid",
  "status":           "failed",
  "error":            "Краткое описание причины (для логов и Telegram-алерта)",
  "schema_version":   "2.0"
}
```

`status` — только `finished` или `failed`. Партиальные результаты не поддерживаются.

> **Транспорт:** LLM-сервис публикует **raw JSON** (без MassTransit envelope), PG-consumer
> настроен на raw-deserialization. `delivery_mode: PERSISTENT`, `content_type:
> application/json; charset=utf-8`, очередь `llm.results` durable.

#### Correlation ID (опционально, рекомендуется на будущее)

Для трассировки запросов через все компоненты в Seq PG использует `AnalysisJobId` как correlation
identifier. На стороне LLM ничего обязательного делать не нужно — мы сами берём `analysis_job_id`
из payload и пишем его в Serilog LogContext.

**Опционально**: если хочешь поддержать стандартный AMQP-correlation (для совместимости с
другими трейсинг-стеками), при публикации можно выставить `correlation_id` на уровне AMQP
message properties:

```python
await channel.default_exchange.publish(
    Message(
        body=json.dumps(response, ensure_ascii=False).encode("utf-8"),
        content_type="application/json",
        delivery_mode=DeliveryMode.PERSISTENT,
        correlation_id=str(job_id),       # ← опционально
    ),
    routing_key=callback_q,
)
```

PG-сторона прочитает его через `ConsumeContext.CorrelationId`. Это делает correlation
протоколо-нативным (не привязан к body-формату). На MVP не обязательно — `analysis_job_id`
в body нам достаточно.

### 5. REST status endpoint

LLM-сервис поднимает HTTP-сервер и отдаёт прогресс по job-у.
PG поллит этот эндпоинт когда нужно показать статус во Frontend (Web API → Status API → опрос LLM).

#### `GET /status/{analysis_job_id}`

```json
{
  "analysis_job_id": "uuid",
  "status":          "processing",
  "stage":           "inferring",
  "progress":        0.45,
  "started_at":      "2026-05-06T10:00:00Z",
  "finished_at":     null,
  "error":           null
}
```

Поля:

| Поле | Тип | Описание |
|---|---|---|
| `status` | string | `processing` / `finished` / `failed` / `unknown` |
| `stage` | string | один из: `received`, `downloading_input`, `inferring`, `uploading_output`, `done` |
| `progress` | float | 0.0–1.0, прогресс **внутри текущей стадии** (или общий — на усмотрение, главное монотонно растёт) |
| `started_at` | ISO 8601 | когда message был получен из RabbitMQ |
| `finished_at` | ISO 8601 / null | заполняется при `status` ∈ `finished`/`failed` |
| `error` | string / null | текст ошибки если `status: "failed"` |

Если job-id неизвестен (никогда не приходил или был удалён из памяти):

```json
HTTP 404
{ "analysis_job_id": "uuid", "status": "unknown" }
```

#### `GET /health/live`

```json
{ "status": "alive" }
```

PG-стек использует это для healthcheck в docker-compose.

---

## Stages (детально)

| Stage | Что происходит | Когда устанавливается |
|---|---|---|
| `received` | message ack-нут, начали обработку | сразу после consume |
| `downloading_input` | `s3.get_object(input_url)` | перед чтением S3 |
| `inferring` | LLM-инференс, может занять минуты | перед циклом по reviews |
| `uploading_output` | `s3.put_object(output_*)` | после завершения LLM |
| `done` | response опубликован в `llm.results` | финал, status='finished' |

При ошибке на любой стадии — `status='failed'`, `stage` остаётся последним достигнутым, заполняется `error`.

---

## Python skeleton

### Зависимости (`requirements.txt`)

```
aio-pika>=9.4
boto3>=1.34
fastapi>=0.110
uvicorn[standard]>=0.27
```

(Дополнительно — твой LLM SDK: `openai`, `anthropic`, `transformers`, etc.)

### `worker.py` — полный пример

```python
import asyncio
import json
import os
from datetime import datetime, timezone
from urllib.parse import urlparse

import boto3
import uvicorn
from aio_pika import connect_robust, Message, IncomingMessage, DeliveryMode
from fastapi import FastAPI, HTTPException

# === Config ===
RABBIT_URL    = os.environ["RABBIT_URL"]
S3_ENDPOINT   = os.environ["S3_ENDPOINT"]
S3_KEY        = os.environ["S3_ACCESS_KEY"]
S3_SECRET     = os.environ["S3_SECRET_KEY"]
S3_BUCKET     = os.environ.get("S3_BUCKET", "obratka-jobs")
LLM_HTTP_PORT = int(os.environ.get("LLM_HTTP_PORT", "8000"))
SCHEMA_VER    = "2.0"

# === In-memory job tracker ===
# MVP: память. Production: Redis / SQLite / file — иначе при рестарте теряется state.
job_state: dict[str, dict] = {}

def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()

def update_state(job_id: str, **kw):
    cur = job_state.setdefault(job_id, {
        "analysis_job_id": job_id,
        "status": "processing",
        "stage": "received",
        "progress": 0.0,
        "started_at": now_iso(),
        "finished_at": None,
        "error": None,
    })
    cur.update(kw)

# === S3 helpers ===
s3 = boto3.client(
    "s3",
    endpoint_url=S3_ENDPOINT,
    aws_access_key_id=S3_KEY,
    aws_secret_access_key=S3_SECRET,
)

def parse_s3_url(url: str) -> tuple[str, str]:
    p = urlparse(url)
    return p.netloc, p.path.lstrip("/")

def s3_get_json(url: str) -> dict:
    bucket, key = parse_s3_url(url)
    return json.loads(s3.get_object(Bucket=bucket, Key=key)["Body"].read())

def s3_put_json(key: str, payload: dict) -> str:
    s3.put_object(
        Bucket=S3_BUCKET,
        Key=key,
        Body=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        ContentType="application/json",
    )
    return f"s3://{S3_BUCKET}/{key}"

# === LLM logic — заглушка, заменить своей ===
def llm_analyze_reviews(reviews: list[dict], job_id: str) -> list[dict]:
    out = []
    total = max(len(reviews), 1)
    for i, r in enumerate(reviews):
        # реальный LLM-вызов здесь
        out.append({
            "review_id": r["review_id"],
            "text": r["text"],
            "overall_sentiment": "позитивный" if (r.get("stars") or 3) >= 4 else "негативный",
            "overall_confidence": 0.7,
            "aspects": [],
        })
        if i % 10 == 0:
            update_state(job_id, progress=round(i / total, 2))
    return out

def llm_build_summary(reviews: list[dict]) -> dict:
    """Job-level recommendations (формат 2.0)."""
    full_recommendations = [
        # пример заглушки — заменить реальной LLM-логикой
        {
            "priority": 1,
            "topic": "персонал",
            "title": "Улучшение коммуникации",
            "body": "Внедрить регулярные тренинги по клиентоориентированности.",
            "expected_impact": "Снижение жалоб, рост повторных визитов.",
            "evidence": [],
        },
    ]
    return {
        "recommendations_count": len(full_recommendations),
        "summary": "TODO: краткое резюме на 1–3 предложения",
        "full_recommendations": full_recommendations,
    }

# === AMQP handler ===
async def handle(channel, message: IncomingMessage):
    async with message.process(requeue=False):
        body    = json.loads(message.body.decode())
        payload = body.get("message", body)              # MassTransit envelope unwrap

        job_id     = payload["analysis_job_id"]
        input_url  = payload["payload_url"]
        callback_q = payload.get("callback_queue", "llm.results")

        update_state(job_id, status="processing", stage="received", progress=0.0)

        try:
            update_state(job_id, stage="downloading_input")
            input_data = s3_get_json(input_url)
            reviews    = input_data.get("reviews", [])

            update_state(job_id, stage="inferring", progress=0.0)
            reviews_out = llm_analyze_reviews(reviews, job_id)
            summary_out = llm_build_summary(reviews)

            update_state(job_id, stage="uploading_output", progress=0.95)
            reviews_url = s3_put_json(
                f"{job_id}/output_reviews.json",
                {
                    "schema_version": SCHEMA_VER,
                    "analysis_job_id": job_id,
                    "reviews": reviews_out,
                },
            )
            summary_url = s3_put_json(
                f"{job_id}/output_summary.json",
                {
                    "schema_version": SCHEMA_VER,
                    "analysis_job_id": job_id,
                    **summary_out,
                },
            )

            response = {
                "analysis_job_id":    job_id,
                "status":             "finished",
                "result_reviews_url": reviews_url,
                "result_summary_url": summary_url,
                "schema_version":     SCHEMA_VER,
            }
            update_state(
                job_id,
                status="finished",
                stage="done",
                progress=1.0,
                finished_at=now_iso(),
            )
        except Exception as e:
            err = f"{type(e).__name__}: {e}"
            response = {
                "analysis_job_id":  job_id,
                "status":           "failed",
                "error":            err,
                "schema_version":   SCHEMA_VER,
            }
            update_state(job_id, status="failed", finished_at=now_iso(), error=err)

        await channel.default_exchange.publish(
            Message(
                body=json.dumps(response, ensure_ascii=False).encode("utf-8"),
                content_type="application/json",
                delivery_mode=DeliveryMode.PERSISTENT,
            ),
            routing_key=callback_q,
        )

async def amqp_loop():
    conn    = await connect_robust(RABBIT_URL)
    channel = await conn.channel()
    await channel.set_qos(prefetch_count=1)              # один job за раз — LLM тяжёлая
    queue = await channel.declare_queue("llm.requests", durable=True)
    await queue.consume(lambda msg: handle(channel, msg))
    print("LLM service listening on llm.requests")
    await asyncio.Future()                                # forever

# === REST API ===
app = FastAPI(title="LLM Service Status API")

@app.get("/health/live")
async def health_live():
    return {"status": "alive"}

@app.get("/status/{analysis_job_id}")
async def get_status(analysis_job_id: str):
    state = job_state.get(analysis_job_id)
    if not state:
        raise HTTPException(
            status_code=404,
            detail={"analysis_job_id": analysis_job_id, "status": "unknown"},
        )
    return state

# === Main ===
async def main():
    config = uvicorn.Config(app, host="0.0.0.0", port=LLM_HTTP_PORT, log_level="info")
    server = uvicorn.Server(config)
    await asyncio.gather(server.serve(), amqp_loop())

if __name__ == "__main__":
    asyncio.run(main())
```

### Запуск

```bash
export RABBIT_URL=amqp://gateway:gateway_pwd@localhost:5672/
export S3_ENDPOINT=http://localhost:9000
export S3_ACCESS_KEY=minioadmin
export S3_SECRET_KEY=minioadmin
export LLM_HTTP_PORT=8000

python worker.py
# Слушает RabbitMQ + поднимает HTTP-сервер на :8000
```

### Проверка REST endpoint

```bash
# Health
curl http://localhost:8000/health/live
# {"status":"alive"}

# Status неизвестного job-а
curl http://localhost:8000/status/00000000-0000-0000-0000-000000000000
# 404 + {"detail":{...,"status":"unknown"}}

# Status во время обработки
curl http://localhost:8000/status/<real-job-id>
# {..., "status": "processing", "stage": "inferring", "progress": 0.45}
```

---

## Деплой как docker-контейнер

`Dockerfile`:
```dockerfile
FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY worker.py .
EXPOSE 8000
CMD ["python", "worker.py"]
```

Добавление в `~/processing-gateway/docker-compose.yml` (или отдельный compose-файл):

```yaml
services:
  llm-service:
    build:
      context: ../llm-service
      dockerfile: Dockerfile
    image: llm-service:latest
    restart: unless-stopped
    environment:
      RABBIT_URL: "amqp://${RABBIT_USER}:${RABBIT_PASS}@rabbitmq:5672/"
      S3_ENDPOINT: "http://minio:9000"
      S3_ACCESS_KEY: "${MINIO_ACCESS_KEY}"
      S3_SECRET_KEY: "${MINIO_SECRET_KEY}"
      S3_BUCKET: "obratka-jobs"
      LLM_HTTP_PORT: "8000"
    networks: [parser-internal]
    # Порт 8000 наружу не выставлять — PG ходит по docker-DNS http://llm-service:8000
```

PG будет ходить за статусом по `http://llm-service:8000/status/<jobId>` через docker DNS.

> При запуске LLM-сервиса нужно **остановить** `llm-stub` (заглушку):
> `docker compose stop llm-stub`. Они оба слушают `llm.requests`, иначе сообщения будут
> разделяться round-robin между ними.

---

## Идемпотентность

PG может перевыпустить запрос (replay через QA-ручку или MassTransit retry).
Тот же `analysis_job_id` может прийти второй раз.

Что должен делать LLM:
- **OK:** перезаписать `output_reviews.json` / `output_summary.json` (S3 PUT идемпотентен по ключу)
- **OK:** опубликовать ответ повторно
- **OK:** обновить `job_state[job_id]` поверх старого состояния
- **НЕ OK:** падать с «уже обработано»

PG со своей стороны делает `INSERT ... ON CONFLICT DO NOTHING` при ингесте — дубликат в БД не возникнет.

---

## Чек-лист интеграции

- [ ] Получить creds (`RABBIT_USER/PASS`, `MINIO_ACCESS_KEY/SECRET_KEY`) у админа PG
- [ ] Локальный smoke-test:
  - `python -c "import aio_pika, asyncio; asyncio.run(aio_pika.connect_robust('$RABBIT_URL').close())"` → без ошибок
  - `aws s3 ls s3://obratka-jobs --endpoint-url=$S3_ENDPOINT` → без ошибок
- [ ] Запустить worker (skeleton как есть, без реальной LLM-логики)
- [ ] Попросить QA запустить тестовый job на dev-VPS:
  ```bash
  curl -X POST 'https://gateway-dev.../api/qa/analyses' -H 'X-Api-Key: ...' -d '{...}'
  ```
- [ ] Убедиться: worker получил message → залил 2 файла → опубликовал ответ → status в PG перешёл в `computing_aggregates` → `completed`
- [ ] Проверить REST: `curl http://llm-service:8000/status/<jobId>` отдаёт корректный stage
- [ ] Подключить реальную LLM-модель (заменить `llm_analyze_reviews` / `llm_build_summary`)
- [ ] Persistent job-state — для production (на MVP можно оставить in-memory, но при рестарте status теряется)

---

## Связанные документы PG-стороны

- `CLAUDE.md` — общая архитектура PG
- `test-cases.md` §2.2 — как QA запускают анализ для тестирования pipeline
