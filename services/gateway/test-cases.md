# Test cases — локальная проверка PG + Parser

> Временный документ. Удалить, когда переедем в постоянный playbook.
>
> Все curl-ы готовы для импорта в Postman: **File → Import → Raw text** или
> **Ctrl/Cmd+L** в адресной строке (paste curl). Postman сам распарсит метод,
> URL, headers, body.
>
> Где встречается `<JOB_ID>` — замени на реальный guid из ответа на ручку
> создания анализа (раздел 2). В Postman удобно завести environment variable
> `{{jobId}}` и подставлять её.

| URL/Сервис | Что |
|-----|-----|
| http://localhost:8081 | Processing Gateway (X-Api-Key: `dev-gateway-api-key`) |
| http://localhost:8080 | Parser Service |
| http://localhost:5342 | Seq (без логина) |
| http://localhost:9001 | MinIO Console (`minioadmin` / `minioadmin`) |
| http://localhost:15672 | RabbitMQ (`gateway` / `gateway_pwd`) |

---

## 1. Sanity — все зависимости

### 1.1 PG жив

```bash
curl --location 'http://localhost:8081/health/live'
```
Ожидание: `200 OK` + `{"status":"alive"}`.

### 1.2 Все зависимости (Postgres + S3 + Parser)

```bash
curl --location 'http://localhost:8081/api/qa/health/dependencies' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Ожидание: `ok=true` для всех трёх. Если `parser=false` — Parser-контейнер ещё стартует, повтори через минуту.

### 1.3 Parser жив через PG-прокси

```bash
curl --location 'http://localhost:8081/api/qa/parser/ping' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Ожидание: `{"ok":true,"note":"Parser ответил 404 на несуществующий task — норма"}`.

### 1.4 Parser напрямую

```bash
curl --location 'http://localhost:8080/api/collection-tasks?limit=5'
```
Ожидание: `{"count":0,"limit":50,"offset":0,"items":[]}`.

### 1.5 Все контейнеры здоровы (shell, не Postman)

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml ps
```
Ожидание: 7 контейнеров `Up` (5 healthy + parser + processing-gateway).

---

## 2. Запуск анализа (главный сценарий)

```bash
curl --location 'http://localhost:8081/api/qa/analyses' \
  --header 'Content-Type: application/json' \
  --header 'X-Api-Key: dev-gateway-api-key' \
  --data '{
    "companyId": "11111111-1111-1111-1111-111111111111",
    "branches": [
      {
        "branchId": "22222222-2222-2222-2222-222222222222",
        "source": "yandex",
        "externalId": "1124715036",
        "externalUrl": "https://yandex.ru/maps/org/artel/1124715036/"
      }
    ]
  }'
```
Ожидание: `202 Accepted` + `{"analysisJobId":"<guid>"}`.

**Запиши `analysisJobId`** — он понадобится во всех следующих ручках.

В Postman: создай environment variable `jobId`, в response-tab поставь test-script:
```js
pm.environment.set("jobId", pm.response.json().analysisJobId);
```

---

## 3. Наблюдение (что произошло)

### 3.1 Снимок job-а в БД (главный отладочный взгляд)

```bash
curl --location 'http://localhost:8081/api/qa/analyses/<JOB_ID>' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Поля: `status`, `collection_progress` (по источникам), `payload_url`, `result_url`, `recommendation`, `error`, временные метки.

### 3.2 Публичный Status API (то, что увидит Frontend)

```bash
curl --location 'http://localhost:8081/api/analyses/<JOB_ID>/status'
```
Stages: `collecting` / `llm_analysis` / `building_dashboard` со state `pending|active|completed|failed`. Без X-Api-Key — это публичный эндпоинт.

### 3.3 Что в S3 для job-а

```bash
curl --location 'http://localhost:8081/api/qa/jobs/<JOB_ID>/blobs' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Со временем должны появиться:
- `<jobId>/raw/yandex.json` — от Parser
- `<jobId>/input.json` — от PG
- `<jobId>/output.json` — от LLM-stub

### 3.4 Содержимое конкретного блоба

```bash
# raw от парсера
curl --location 'http://localhost:8081/api/qa/jobs/<JOB_ID>/blobs/raw/yandex' \
  --header 'X-Api-Key: dev-gateway-api-key'

# input для LLM
curl --location 'http://localhost:8081/api/qa/jobs/<JOB_ID>/blobs/input' \
  --header 'X-Api-Key: dev-gateway-api-key'

# output от LLM
curl --location 'http://localhost:8081/api/qa/jobs/<JOB_ID>/blobs/output' \
  --header 'X-Api-Key: dev-gateway-api-key'
```

### 3.5 Что собрано в БД

```bash
curl --location 'http://localhost:8081/api/qa/analyses/<JOB_ID>/reviews?limit=5' \
  --header 'X-Api-Key: dev-gateway-api-key'
```

---

## 4. Timeline — что должно произойти

| Время | `status` | Что в этот момент происходит |
|-------|----------|------------------------------|
| 0s | `collecting` | StartAnalysisCommand принят, Parser-task создан |
| ~5–60s | `collecting` | Parser реально парсит (Playwright, прокси, scroll) |
| при завершении сбора | `sent_to_llm` | raw/yandex.json в S3, PG ингестил отзывы, input.json опубликован в LLM |
| ~5s после | `computing_aggregates` | LLM-stub синтезировал output.json, ингест прошёл |
| через 1 минуту | `completed` | AutoFinalizeService финализировал (имитация Web API Analytics) |

**Без прокси у Parser:** на шаге `collecting` источник упадёт (SmartCaptcha от Yandex), job переключится в `failed` через ~1–2 мин — валидный failure-path.

**С прокси:** полный happy-path до `completed` за 2–6 минут.

---

## 5. UI-наблюдение

### Seq (http://localhost:5342)

Поиск по конкретному job-у:
```
AnalysisJobId = '<JOB_ID>'
```
Покажет полный путь сообщения через все компоненты (PG / LlmStub / Parser).

Полезные фильтры:
- `Service = 'ProcessingGateway'` — только PG
- `Service = 'LlmStub'` — только стуб
- `@Level = 'Error'` — ошибки
- `@Exception is not null` — все исключения

### MinIO Console (http://localhost:9001)

`Buckets → obratka-jobs → <jobId>/...` — посмотреть содержимое S3-блобов глазами.

### RabbitMQ Management (http://localhost:15672)

`Queues` — состояние очередей: `llm.requests`, `llm.results`, MassTransit consumer-очереди (`StartAnalysisCommand`, `LlmResultMessage`, `AggregatesReadyEvent`).

---

## 6. Альтернативные сценарии (отладочные QA-ручки)

### 6.1 Принудительный fail job-а

```bash
curl --location --request POST 'http://localhost:8081/api/qa/analyses/<JOB_ID>/cancel?reason=manual+test' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Job → `failed`, `error="manual test"`.

### 6.2 Принудительный finalize (имитация AggregatesReadyEvent от Web API)

```bash
curl --location --request POST 'http://localhost:8081/api/qa/analyses/<JOB_ID>/finalize' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Job из `computing_aggregates` → `completed` / `partial`. Не дожидаясь AutoFinalize-таймера.

### 6.3 Имитация ответа LLM напрямую (тест ингеста output)

```bash
curl --location 'http://localhost:8081/api/qa/llm/inject/<JOB_ID>' \
  --header 'Content-Type: application/json' \
  --header 'X-Api-Key: dev-gateway-api-key' \
  --data '{
    "schema_version": "1.0",
    "analysis_job_id": "<JOB_ID>",
    "recommendation": "manual injected output",
    "processedReview": []
  }'
```
PG примет это как «LLM сказал finished», ингестит, переведёт в `computing_aggregates`. Через минуту AutoFinalize → `completed`.

### 6.4 Имитация LLM failure

```bash
curl --location --request POST 'http://localhost:8081/api/qa/llm/fail/<JOB_ID>?error=manual+fail' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Job переходит в `failed`, публикуется AnalysisCompletedEvent(failed).

### 6.5 Manual replay LLM (после ручного fail-а или зависшего job-а)

```bash
curl --location --request POST 'http://localhost:8081/api/qa/llm/replay/<JOB_ID>' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Заново соберёт input.json по reviews из БД, опубликует LLM-request.

### 6.6 Сдвинуть sent_at в прошлое (заготовка для будущего reconciler-а)

```bash
curl --location --request POST 'http://localhost:8081/api/qa/llm/timeout/<JOB_ID>' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Полезно для отладки timeout-сценариев когда Reconciler будет реализован (Этап 7).

### 6.7 Состояние MassTransit Outbox

```bash
curl --location 'http://localhost:8081/api/qa/outbox' \
  --header 'X-Api-Key: dev-gateway-api-key'
```
Поможет понять «сообщение опубликовано, но осело в outbox-таблице».

---

## 7. Быстрый чек-лист

Запусти job (раздел 2), потом каждые 30 сек делай раздел 3.1. Ожидай переходов:

- `pending` → `collecting` (мгновенно)
- `collecting` → `failed` (если без прокси у Parser-а)
- `collecting` → `sent_to_llm` → `computing_aggregates` → `completed` (если с прокси)

**Если зависло на `collecting` дольше 2 минут** — Parser работает, ждёт. Логи:

```bash
docker compose -f docker-compose.yml -f docker-compose.full.yml logs --tail=50 parser
```

**Если что-то падает где-то в pipeline** — Seq по `AnalysisJobId` покажет где именно.

---

## 8. Очистка / рестарт

```bash
# Остановить + СТЕРЕТЬ volumes (Postgres, MinIO, Seq, SQLite парсера)
docker compose -f docker-compose.yml -f docker-compose.full.yml down -v

# Заново
docker compose -f docker-compose.yml -f docker-compose.full.yml up -d --build
```

`-v` важен при изменении схемы БД (миграций) — без него старые таблицы блокируют новую `Initial`.

---

## Postman-tips

1. **Environment.** Создай окружение `local` с переменными:
   - `baseUrl` = `http://localhost:8081`
   - `parserUrl` = `http://localhost:8080`
   - `apiKey` = `dev-gateway-api-key`
   - `jobId` = (пусто, заполняется автоматически после запуска анализа)

2. **Авто-сохранение jobId.** На запросе из раздела 2 в **Tests** добавь:
   ```js
   pm.environment.set("jobId", pm.response.json().analysisJobId);
   ```
   Тогда во всех остальных запросах `<JOB_ID>` заменяй на `{{jobId}}`.

3. **Авторизация.** В коллекции на уровне folder поставь Authorization → API Key:
   - Key: `X-Api-Key`
   - Value: `{{apiKey}}`
   - Add to: Header
   - Тогда в каждом запросе руками не повторять.

4. **Импорт сразу всего блока curl.** Postman → Import → Raw text → вставь любой curl выше → Continue → Import.
