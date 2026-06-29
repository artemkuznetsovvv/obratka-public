# Test cases — VPS-стенд PG + Parser

> Playbook для проверки Processing Gateway на dev-VPS
> (`gateway-dev.193.233.217.223.sslip.io`).
>
> Все curl-ы готовы для импорта в Postman: **File → Import → Raw text** или
> **Ctrl/Cmd+L** в адресной строке (paste curl).
>
> Где встречается `<JOB_ID>` — замени на реальный guid из ответа на ручку
> создания анализа (раздел 2). В Postman удобно завести environment variable
> `{{jobId}}` и подставлять её.
>
> Где `<GATEWAY_API_KEY>` — значение из `~/processing-gateway/.env` на VPS,
> ключ `GATEWAY_API_KEY`.

| URL | Назначение | Доступ |
|-----|-----|-----|
| https://gateway-dev.193.233.217.223.sslip.io | Processing Gateway | IP allowlist + `X-Api-Key` для QA-ручек |
| https://parser.193.233.217.223.sslip.io | Parser Service (прямой) | IP allowlist + `X-Api-Key` |
| https://logs.193.233.217.223.sslip.io | Seq | IP allowlist |
| https://s3-admin.193.233.217.223.sslip.io | MinIO Web Console | IP allowlist + MinIO credentials |

> RabbitMQ Management на VPS наружу **не выставлен** — для просмотра очередей
> используй SSH-туннель (см. §5 ниже).

---

## 1. Sanity — все зависимости

### 1.1 PG жив

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/health/live'
```
Ожидание: `200 OK` + `{"status":"alive"}`.

### 1.2 Все зависимости (Postgres + S3 + Parser)

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/health/dependencies' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Ожидание: `ok=true` для всех трёх. Если `parser=false` — Parser-контейнер ещё стартует или сеть `parser-service_internal` не подцепилась.

### 1.3 Parser жив через PG-прокси

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/parser/ping' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Ожидание: `{"ok":true,"note":"Parser ответил 404 на несуществующий task — норма"}`.

### 1.4 Parser напрямую (через свой vhost)

```bash
curl --location 'https://parser.193.233.217.223.sslip.io/api/collection-tasks?limit=5'
```
Ожидание: `{"count":N,"limit":50,"offset":0,"items":[...]}` — список последних task-ов парсера.

### 1.5 Все контейнеры здоровы (на VPS)

```bash
ssh deploy@193.233.217.223
cd ~/processing-gateway && docker compose ps
cd ~/parser-service     && docker compose ps
```
Ожидание:
- В parser-стеке: `parser`, `minio`, `rabbitmq`, `seq`, `nginx`, `certbot` — все Up.
- В PG-стеке: `processing-gateway`, `processing-db` (healthy), `llm-stub` — все Up.

---

## 2. Запуск анализа (главный сценарий)

### 2.1. Один источник (быстрая проверка)

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses' \
  --header 'Content-Type: application/json' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>' \
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

**Запиши `analysisJobId`** — он нужен во всех следующих ручках.

В Postman: на запросе в **Tests** добавь:
```js
pm.environment.set("jobId", pm.response.json().analysisJobId);
```

### 2.2. Три источника одновременно (одна компания, один филиал)

Семантика: одна физическая точка представлена в **2gis + Yandex + Google** под разными `externalId`.
Все три обращения уходят в Parser параллельно (один POST на источник, общий `jobId`).

| Поле | Где взять |
|-----|-----------|
| `companyId` | любой UUID, **один на запрос** |
| `branchId` | UUID **физического** филиала, один и тот же для всех трёх источников |
| `externalId` 2gis | из URL `https://2gis.ru/.../firm/<ID>`, числовой |
| `externalId` Yandex | из URL `https://yandex.ru/maps/org/.../<ID>/`, числовой |
| `externalId` Google | из URL `https://www.google.com/maps/place/.../data=!4m...`, Place ID `ChIJ...` |

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses' \
  --header 'Content-Type: application/json' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>' \
  --data '{
    "companyId": "aaaaaaaa-1111-1111-1111-111111111111",
    "branches": [
      {
        "branchId":   "bbbbbbbb-2222-2222-2222-222222222222",
        "source":     "2gis",
        "externalId": "70000001008756891",
        "externalUrl": "https://2gis.ru/moscow/firm/70000001008756891"
      },
      {
        "branchId":   "bbbbbbbb-2222-2222-2222-222222222222",
        "source":     "yandex",
        "externalId": "1124715036",
        "externalUrl": "https://yandex.ru/maps/org/artel/1124715036/"
      },
      {
        "branchId":   "bbbbbbbb-2222-2222-2222-222222222222",
        "source":     "google",
        "externalId": "ChIJxxxxxxxxxxxxxxxxxxxxxxxxx",
        "externalUrl": "https://www.google.com/maps/place/.../data=!4m..."
      }
    ]
  }'
```

**Тайминг:** 5–20 минут полный happy-path. Yandex обычно дольше всех (Playwright scroll до 600 отзывов).

---

## 3. Наблюдение (что произошло)

### 3.1 Снимок job-а в БД (главный отладочный взгляд)

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses/<JOB_ID>' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Поля: `status`, `collection_progress` (по источникам, с `task_id`/`progress`/`error`), `payload_url`, `result_url`, `recommendation`, временные метки.

### 3.2 Публичный Status API (то, что увидит Frontend)

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/analyses/<JOB_ID>/status'
```
Без `X-Api-Key` — публичный эндпоинт (защищён только nginx allowlist).

Stages: `collecting` / `llm_analysis` / `building_dashboard` со state `pending|active|completed|failed`. В `sources` — рендер JSONB `collection_progress` (per source: `taskId`, `status`, `progress` 0–100, `reviewCount`).

### 3.3 Что в S3 для job-а

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/jobs/<JOB_ID>/blobs' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Со временем должны появиться:
- `<jobId>/raw/2gis.json`, `<jobId>/raw/yandex.json`, `<jobId>/raw/google.json` — от Parser
- `<jobId>/input.json` — от PG (после агрегации)
- `<jobId>/output.json` — от LLM-stub

Альтернатива — через MinIO Web Console: `https://s3-admin.193.233.217.223.sslip.io` → bucket `obratka-jobs` → префикс `<jobId>/`.

### 3.4 Содержимое конкретного блоба

```bash
# raw от парсера (один источник)
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/jobs/<JOB_ID>/blobs/raw/yandex' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'

# input для LLM
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/jobs/<JOB_ID>/blobs/input' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'

# output от LLM
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/jobs/<JOB_ID>/blobs/output' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```

### 3.5 Что собрано в БД

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses/<JOB_ID>/reviews?limit=5' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```

---

## 4. Timeline — что должно произойти

| Время | `status` | Что в этот момент происходит |
|-------|----------|------------------------------|
| 0s | `collecting` | StartAnalysisCommand принят, Parser-task'и созданы (один на источник) |
| 30s–15мин | `collecting` | Parser реально парсит (Playwright + прокси-ротация). Yandex дольше всех |
| при завершении сбора | `sent_to_llm` | raw/<source>.json в S3, PG ингестил отзывы, input.json опубликован в LLM |
| ~5s после | `computing_aggregates` | LLM-stub синтезировал output.json, ингест прошёл |
| ~5 минут после | `completed` | AutoFinalizeService финализировал (имитация Web API Analytics) |

**Изоляция сбоев** (ADR-001 §10): если 1 из 3 источников `failed` (плагин не справился, CAPTCHA), а 2 успешны — финальный `status='partial'`, pipeline отрабатывает на собранных отзывах.

---

## 5. UI-наблюдение

### Seq (https://logs.193.233.217.223.sslip.io)

Поиск по конкретному job-у:
```
AnalysisJobId = '<JOB_ID>'
```

Полезные фильтры:
- `Service = 'ProcessingGateway'` — только PG
- `Service = 'LlmStub'` — только стуб
- `Service = 'ParserService'` — только парсер
- `@Level = 'Error'` — ошибки
- `@Exception is not null` — все исключения

### MinIO Console (https://s3-admin.193.233.217.223.sslip.io)

`Buckets → obratka-jobs → <jobId>/...` — посмотреть содержимое S3-блобов глазами.
Логин: `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` из `~/parser-service/.env`.

### RabbitMQ Management (через SSH-туннель)

Снаружи не выставлен. Туннель с локальной машины:

```bash
ssh -L 15672:localhost:15672 deploy@193.233.217.223
# Откроет туннель, пока сессия активна.

# В другом окне на локальной машине:
# Сначала временно открой порт RabbitMQ на loopback VPS:
docker compose -f ~/parser-service/docker-compose.yml exec rabbitmq \
    sh -c 'echo "Management web на порту 15672 внутри контейнера"'
# (По умолчанию rabbitmq-management слушает 15672 внутри)
```

Альтернатива — через CLI:
```bash
ssh deploy@193.233.217.223 \
    "docker compose -f ~/parser-service/docker-compose.yml exec rabbitmq \
     rabbitmqctl list_queues name messages messages_ready"
```
Поможет понять, висят ли сообщения в `llm.requests` / `llm.results` / outbox.

### Postgres напрямую (через SSH-туннель)

```bash
# Локально (Git Bash):
ssh -L 5433:localhost:5433 deploy@193.233.217.223
```

> Чтобы туннель работал, на VPS в `~/processing-gateway/docker-compose.yml`
> временно раскомментируй / добавь:
> ```yaml
> processing-db:
>   ports:
>     - "127.0.0.1:5433:5432"   # только loopback, снаружи не виден (cloud-firewall режет)
> ```
> Применить: `docker compose up -d processing-db`. После работы — убрать обратно.

Дальше в DBeaver / psql: `localhost:5433`, db `processing`, user `processing_user`, password из `.env`.

SQL для агрегации по job-у:
```sql
SELECT r.source, COUNT(*) AS reviews,
       COUNT(rlr.id) AS analyzed,
       AVG(r.stars) AS avg_stars
FROM reviews r
LEFT JOIN review_llm_results rlr ON rlr.review_id = r.id
WHERE r.id IN (
    SELECT review_id FROM analysis_job_reviews
    WHERE analysis_job_id = '<JOB_ID>'
)
GROUP BY r.source;
```

---

## 6. Альтернативные сценарии (отладочные QA-ручки)

### 6.1 Принудительный fail job-а

```bash
curl --location --request POST 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses/<JOB_ID>/cancel?reason=manual+test' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Job → `failed`, `error="manual test"`.

### 6.2 Принудительный finalize (имитация AggregatesReadyEvent от Web API)

```bash
curl --location --request POST 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses/<JOB_ID>/finalize' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Job из `computing_aggregates` → `completed` / `partial`. Не дожидаясь AutoFinalize-таймера.

### 6.3 Имитация ответа LLM напрямую (тест ингеста output)

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/llm/inject/<JOB_ID>' \
  --header 'Content-Type: application/json' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>' \
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
curl --location --request POST 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/llm/fail/<JOB_ID>?error=manual+fail' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Job переходит в `failed`, публикуется `AnalysisCompletedEvent(failed)`.

### 6.5 Manual replay LLM (после ручного fail-а или зависшего job-а)

```bash
curl --location --request POST 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/llm/replay/<JOB_ID>' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Заново соберёт input.json по reviews из БД, опубликует LLM-request.

### 6.6 Сдвинуть sent_at в прошлое (заготовка для будущего reconciler-а)

```bash
curl --location --request POST 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/llm/timeout/<JOB_ID>' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Полезно для отладки timeout-сценариев когда Reconciler будет реализован (Этап 7).

### 6.7 Состояние MassTransit Outbox

```bash
curl --location 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/outbox' \
  --header 'X-Api-Key: <GATEWAY_API_KEY>'
```
Поможет понять «сообщение опубликовано, но осело в outbox-таблице».

---

## 7. Быстрый чек-лист

Запусти job (раздел 2.1 или 2.2), потом каждые 30 сек делай раздел 3.1 или 3.2. Ожидай переходов:

- `pending` → `collecting` (мгновенно)
- `collecting` → `failed` (если плагин Parser-а не справился, нет прокси, CAPTCHA блок)
- `collecting` → `partial` (если 1+ источник упал, но другие успешны)
- `collecting` → `sent_to_llm` → `computing_aggregates` → `completed` (happy-path)

**Если зависло на `collecting` дольше 15 минут** — Parser работает, Yandex может scroll-ить долго. Проверь логи:

```bash
ssh deploy@193.233.217.223 \
    "docker compose -f ~/parser-service/docker-compose.yml logs --tail=50 parser"
```

**Если что-то падает где-то в pipeline** — Seq по `AnalysisJobId` покажет, где именно.

---

## 8. Перезапуск / обновление PG на VPS

### Обновить только код (rsync + rebuild)

С локальной машины (Git Bash):
```bash
cd /c/Users/nordWorkStudy/Desktop/Obratka/Processing-Gateway

rsync -avz --delete \
    --exclude='.git' --exclude='bin' --exclude='obj' --exclude='data' \
    --exclude='tests' --exclude='docker-compose*.yml' --exclude='parser-config' \
    --exclude='/*.md' \
    ./ deploy@193.233.217.223:~/processing-gateway/app/

ssh deploy@193.233.217.223 '
    cd ~/processing-gateway &&
    docker compose build processing-gateway llm-stub &&
    docker compose up -d processing-gateway llm-stub
'
```

EF-миграции применяются автоматически при старте контейнера PG.

### Перечитать конфиг nginx (после правки vhost-ов)

```bash
ssh deploy@193.233.217.223 '
    cd ~/parser-service &&
    docker compose exec nginx nginx -t &&
    docker compose exec nginx nginx -s reload
'
```

### Полный рестарт (без удаления данных)

```bash
ssh deploy@193.233.217.223 '
    cd ~/processing-gateway &&
    docker compose down &&
    docker compose up -d
'
```
**НЕ используй `down -v`** — это сотрёт все данные Postgres PG. Если нужно почистить БД — точечно через psql, не через volume-wipe.

---

## Postman-tips

1. **Environment.** Создай окружение `vps` с переменными:
   - `baseUrl` = `https://gateway-dev.193.233.217.223.sslip.io`
   - `apiKey` = `<значение GATEWAY_API_KEY из .env на VPS>`
   - `companyId` = `aaaaaaaa-1111-1111-1111-111111111111` (или свой)
   - `branchId` = `bbbbbbbb-2222-2222-2222-222222222222` (или свой)
   - `jobId` = (пусто, заполнится автоматически после запуска анализа)

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

5. **Runner для Status API.** Чтобы автоматически опрашивать прогресс job-а:
   - Запрос **Status (public)**: `GET {{baseUrl}}/api/analyses/{{jobId}}/status`, без headers
   - Postman Runner → выбрать запрос → Iterations: 30, Delay: 30000 (ms)
   - Получишь полный жизненный цикл job-а в виде серии ответов

6. **Альтернатива через интерфейс.** Если не хочешь Postman — открой в браузере:
   `https://gateway-dev.193.233.217.223.sslip.io/api/analyses/<JOB_ID>/status` — это публичный эндпоинт без auth.
