# LLM Output — финальный контракт (для команды Processing Gateway)

> Документ фиксирует **актуальные** форматы выходов LLM-сервиса.
> Все примеры — реальные срезы из прогона `analysis_job_id=28d98c2e-eb09-4556-8b35-0f82bc5f1e09`
> через OpenRouter (229 отзывов клиники, ~104 секунды, schema_version `2.0`).
>
> Источники, на основе которых выстроен контракт:
> - `LLM_PYTHON_QUICKSTART.md` — канон для `output_reviews.json` и `LlmResultMessage`.
> - `tasks/codex_reviews_analysis_requirements.md` (§2) — канон для `output_summary.json`.
>
> Если какие-то поля у вас в DTO PG отличаются от описанных ниже — приведите их к этому
> документу, либо сообщите, и мы обсудим.

---

## TL;DR — что изменилось относительно `LLM_PYTHON_QUICKSTART.md`

| Артефакт | Было в QUICKSTART | Стало (актуально) |
|---|---|---|
| `output_reviews.json` | как в QUICKSTART §3.1 | **без изменений** — sentiment-ы на русском (`позитивный`/`негативный`/`нейтральный`), aspect-объекты с `topic`/`sentiment`/`confidence`/`fragment`/`is_freeform` |
| `output_reviews.json` → `review_id` | в примере была строка | возвращается **в исходном типе из input.json** (если PG прислал `int64` — будет `int64`, если строку — будет строка) |
| `output_summary.json` | `recommendation` (строкой) + `summary_stats { total_reviews, sentiment_distribution, top_topics }` | **`recommendations_count` + `summary` + `full_recommendations[]`** (формат codex §2). `summary_stats` и поле `recommendation` — **больше не выводятся**. |
| `LlmResultMessage` в `llm.results` | как в QUICKSTART §4 | **без изменений** |

> Для PG это означает: ничего ломать в обработке `output_reviews.json` не нужно.
> Перепиливать парсинг придётся только для `output_summary.json` — теперь там
> массив структурированных рекомендаций вместо одной строки.

---

## 1. `output_reviews.json` (S3, формат QUICKSTART 2.0)

**Ключ в S3:** `s3://obratka-jobs/<analysis_job_id>/output_reviews.json`

### Схема

```json
{
  "schema_version":  "2.0",
  "analysis_job_id": "<UUID, тот же что в input.json>",
  "reviews": [
    {
      "review_id":          "<int64 | string — исходный тип из input.json>",
      "text":               "<исходный текст отзыва, не модифицируется>",
      "overall_sentiment":  "позитивный | негативный | нейтральный",
      "overall_confidence": "<float, 0.0..1.0>",
      "aspects": [
        {
          "topic":       "<string — тема, например 'персонал', 'атмосфера', 'качество лечения'>",
          "sentiment":   "позитивный | негативный | нейтральный",
          "confidence":  "<float, 0.0..1.0>",
          "fragment":    "<подстрока исходного text — фактическая цитата, не пересказ>",
          "is_freeform": "<bool — true, если topic не из закрытого справочника тем>"
        }
      ]
    }
  ]
}
```

### Гарантии

- Каждому входному отзыву соответствует **ровно один** объект в `reviews[]`.
- Сортировка `reviews[]` совпадает с сортировкой во входном `input.json`.
- `review_id` сохраняется в **исходном типе**: если в input был `int64` — на выходе тоже `int`, не строка. Это критично для матчинга в БД PG.
- `text` — исходный без потери эмодзи, переносов строк, пунктуации.
- `overall_sentiment` — одно из трёх русских значений из закрытого списка. Промежуточные модельные оценки (`mixed`, `unknown`) сворачиваются в `нейтральный`.
- `aspects[]` — пустой массив допустим (если текст пустой или ни один тема не извлеклась с достаточной уверенностью). Минимума по количеству нет.
- `confidence` всегда число в `[0, 1]`, не строка.

### Пример — позитивный отзыв с двумя аспектами

```json
{
  "review_id": 3,
  "text": "Хорошая клиника, прием прошел комфортно, врач очень внимательный .",
  "overall_sentiment": "позитивный",
  "overall_confidence": 0.85,
  "aspects": [
    {
      "topic": "персонал",
      "sentiment": "позитивный",
      "confidence": 0.85,
      "fragment": "",
      "is_freeform": false
    },
    {
      "topic": "атмосфера",
      "sentiment": "позитивный",
      "confidence": 0.8,
      "fragment": "",
      "is_freeform": false
    }
  ]
}
```

### Пример — негативный отзыв со смешанными аспектами

```json
{
  "review_id": 85,
  "text": "Думаю отзывы тут накрученные )\nМне не понравился визит в эту клинику , именно отношение персонала , там была новенькая администратор , вот она очень приветливая была, остальные нет\nСама Клиника не располагает к посещению - дизайн , интерьер .",
  "overall_sentiment": "негативный",
  "overall_confidence": 0.8,
  "aspects": [
    {
      "topic": "персонал",
      "sentiment": "негативный",
      "confidence": 0.9,
      "fragment": "именно отношение персонала",
      "is_freeform": false
    },
    {
      "topic": "персонал",
      "sentiment": "позитивный",
      "confidence": 0.8,
      "fragment": "новенькая администратор , вот она очень приветливая была",
      "is_freeform": false
    },
    {
      "topic": "атмосфера",
      "sentiment": "негативный",
      "confidence": 0.8,
      "fragment": "Сама Клиника не располагает к посещению - дизайн , интерьер",
      "is_freeform": false
    }
  ]
}
```

> Важное наблюдение: для одного `review_id` могут быть **несколько aspect-ов с одной и той же `topic`** и разными `sentiment`. PG-сторона должна это учитывать (например, агрегировать как «положительные/отрицательные упоминания темы X»).

### Пример — отзыв без извлечённых аспектов

```json
{
  "review_id": 17,
  "text": "Лечила глубокий кариес у Мередовой Алсу. Всё прошло спокойно, без «долго и больно». В кресле было комфортно, и зуб потом не ныл, как это часто бывает. Рада, что попала именно сюда)",
  "overall_sentiment": "нейтральный",
  "overall_confidence": 0.0,
  "aspects": []
}
```

> `overall_confidence: 0.0` + пустые `aspects` означают «модель не нашла уверенного сигнала по теме». Это допустимое состояние, не ошибка.

### Распределение в проде (для калибровки PG-дашборда)

На выборке 229 отзывов клиники получилось:
- 181 `позитивный` (79%)
- 41 `нейтральный` (18%)
- 7 `негативный` (3%)
- 414 aspect-объектов на 229 отзывов (~1.8 на отзыв в среднем)

---

## 2. `output_summary.json` (S3, формат codex §2)

**Ключ в S3:** `s3://obratka-jobs/<analysis_job_id>/output_summary.json`

### Схема

```json
{
  "schema_version":        "2.0",
  "analysis_job_id":       "<UUID, тот же что в input.json>",
  "recommendations_count": "<int — равен len(full_recommendations)>",
  "summary":               "<string — краткое резюме на 1–3 предложения>",
  "full_recommendations": [
    {
      "priority":        "<int, 1..3 — 1 наивысший приоритет>",
      "topic":           "<string — тема рекомендации>",
      "title":           "<string — заголовок рекомендации>",
      "body":            "<string — что именно делать>",
      "expected_impact": "<string — ожидаемый эффект для бизнеса>",
      "evidence":        ["<string — короткие цитаты или ссылки на review_id>"]
    }
  ]
}
```

### Гарантии

- `recommendations_count` всегда равен `len(full_recommendations)`. PG может проверять это как инвариант.
- `full_recommendations[]` отсортирован по `priority` **по возрастанию**, при равенстве — больше `evidence` сверху.
- Шкала `priority`:
  - **1** — критично: безопасность, репутация, повторные визиты, медицинское качество.
  - **2** — важно: клиентский опыт, конверсия, ожидание, чистота, коммуникация.
  - **3** — полезно: маркетинг, удержание, операционные улучшения.
- `evidence[]` может быть пустым массивом, но не отсутствовать.
- `summary` всегда строка. Для пустого/невалидного входа — fallback-текст вида «Недостаточно данных для формирования рекомендаций».
- `full_recommendations` может быть пустым массивом (для пустого `reviews[]` или если данных не хватает) — тогда `recommendations_count = 0`.

### Пример — реальный output (первые 2 из 8 рекомендаций)

```json
{
  "schema_version": "2.0",
  "analysis_job_id": "28d98c2e-eb09-4556-8b35-0f82bc5f1e09",
  "recommendations_count": 8,
  "summary": "На основе анализа отзывов и KPI, ключевые проблемы связаны с персоналом, системой записи и прозрачностью цен. Рекомендуется внедрить долгосрочные изменения в обучении персонала и системе записи, а также оперативно улучшить коммуникацию с клиентами.",
  "full_recommendations": [
    {
      "priority": 1,
      "topic": "персонал",
      "title": "Улучшение коммуникации и обучения персонала",
      "body": "На основе отзывов, персонал часто не уделяет достаточного внимания клиентам и плохо объясняет информацию. Рекомендуется внедрить регулярные тренинги по коммуникации и клиентоориентированности.",
      "expected_impact": "Улучшение восприятия клиентами качества обслуживания и уменьшение жалоб на персонал.",
      "evidence": [
        "врач максимально не хотел уделять внимание, даже направления были написаны плохо",
        "я считаю безобразием то, что я должна уточнять ходить ходить уточнять информацию по последующей диагностике.",
        "девушки на регистратуре общаются с клиентами так, что приходится из них информацию всю выуживать"
      ]
    },
    {
      "priority": 1,
      "topic": "персонал",
      "title": "Внедрение системы обратной связи для персонала",
      "body": "Создать механизм, позволяющий клиентам оставлять отзывы о конкретных сотрудниках, чтобы оперативно выявлять и решать проблемы.",
      "expected_impact": "Быстрое выявление и устранение проблем в обслуживании.",
      "evidence": [
        "врач максимально не хотел уделять внимание, даже направления были написаны плохо",
        "девушки на регистратуре общаются с клиентами так, что приходится из них информацию всю выуживать"
      ]
    }
  ]
}
```

### Чего больше **нет**

- ❌ `recommendation` (строкой) — больше **не выводится**. Если PG ожидает это поле для PDF-отчёта — он должен либо собрать строку из `full_recommendations[]` сам, либо использовать `summary` напрямую.
- ❌ `summary_stats { total_reviews, sentiment_distribution, top_topics }` — больше **не выводится**. Если PG нужны эти числа для дашборда, агрегацию проще делать на стороне PG из `output_reviews.json` (сейчас уже есть `reviews_count`, sentiment-distribution считается одним проходом).

Если эти поля критичны для PG — скажите, добавим обратно как дополнительные (мы их легко собираем из готовых данных).

---

## 3. `LlmResultMessage` в очереди `llm.results` (формат QUICKSTART §4)

После успешной загрузки обоих файлов в S3 LLM-сервис публикует ответ в `callback_queue`
(сейчас всегда `llm.results`).

### Схема (success)

```json
{
  "analysis_job_id":    "<UUID>",
  "status":             "finished",
  "result_reviews_url": "s3://obratka-jobs/<jobId>/output_reviews.json",
  "result_summary_url": "s3://obratka-jobs/<jobId>/output_summary.json",
  "schema_version":     "2.0"
}
```

### Схема (failure)

```json
{
  "analysis_job_id": "<UUID>",
  "status":          "failed",
  "error":           "<string — краткая причина для алерта/лога>",
  "schema_version":  "2.0"
}
```

### Пример (success, реальный)

> Это вывод из QA-ручки `/qa/analyze` — там вместо `s3://` URL-ы заменены на `file://`,
> но **в проде это будут `s3://obratka-jobs/<jobId>/...`** ровно той же формы.

```json
{
  "analysis_job_id": "28d98c2e-eb09-4556-8b35-0f82bc5f1e09",
  "status": "finished",
  "result_reviews_url": "file:///app/qa_outputs/28d98c2e-eb09-4556-8b35-0f82bc5f1e09/output_reviews.json",
  "result_summary_url": "file:///app/qa_outputs/28d98c2e-eb09-4556-8b35-0f82bc5f1e09/output_summary.json",
  "schema_version": "2.0"
}
```

### Транспорт

- Сейчас публикация — **raw JSON** (без MassTransit envelope). Если PG-сторона хочет envelope — обернём, это две строки в worker. Скажите.
- `delivery_mode: PERSISTENT`, очередь `llm.results` — `durable=true`. Ответ переживёт рестарт брокера.
- `content_type: application/json; charset=utf-8`.

---

## 4. REST status endpoint (QUICKSTART §5, без изменений)

`GET http://<llm-service>:8000/status/{analysis_job_id}` возвращает прогресс:

```json
{
  "analysis_job_id": "28d98c2e-eb09-4556-8b35-0f82bc5f1e09",
  "status":          "processing | finished | failed",
  "stage":           "received | downloading_input | inferring | uploading_output | done",
  "progress":        0.0,
  "started_at":      "2026-05-06T10:00:00+00:00",
  "finished_at":     null,
  "error":           null
}
```

Если `analysis_job_id` неизвестен → HTTP 404 + тело `{ "analysis_job_id": "...", "status": "unknown" }`.

`GET /health/live` → `{ "status": "alive" }` для docker healthcheck.

---

## 5. Edge cases (критично для PG-стороны)

| Сценарий | Поведение LLM-сервиса |
|---|---|
| `reviews: []` в input | `output_reviews.reviews = []`, `output_summary.recommendations_count = 0`, `full_recommendations = []`, `status = finished` |
| Отзыв с пустым `text` | в выходе будет объект с `overall_sentiment="нейтральный"`, `overall_confidence=0.0`, `aspects=[]`. Отзыв **не теряется** |
| Несовпадение `analysis_job_id` в input.json и в AMQP | `status = failed`, `error = "input.json analysis_job_id mismatch: expected X, got Y"` |
| Битый JSON в S3 | `status = failed`, `error` содержит сообщение десериализации |
| Replay (тот же `analysis_job_id` приходит повторно) | output-файлы перезаписываются (S3 PUT идемпотентен), ответ публикуется заново. PG должен глотать дубль через UNIQUE constraint |
| Один и тот же `topic` в нескольких `aspects` одного review | допускается; PG агрегирует |
| `aspects[].fragment` пустой | допускается (модель не нашла однозначной цитаты, но извлекла тему) |

---

## 6. Версионирование

`schema_version` сейчас `"2.0"` во всех артефактах. Политика:

- Новые поля сверх описанных — **не меняют** версию. PG должен игнорировать unknown fields.
- Удаление required-поля или смена типа — major bump (`"3.0"`) с предварительной координацией.
- Если PG получит `schema_version` старше `2.0` — лучше отвергнуть в `failed` с понятным error.

---

## 7. Где посмотреть руками

В QA-режиме (см. `docker-compose.yml`) можно прогнать любой `input.json` через `POST /qa/analyze`:

```powershell
curl.exe -X POST "http://localhost:8000/qa/analyze?engine=llm" `
  -H "Content-Type: application/json" `
  --data-binary "@input.json"
```

В ответе придёт `output_dir` — там будут все три файла из этого документа в реальном виде.

Для smoke-теста без реальных LLM-вызовов:

```powershell
curl.exe -X POST "http://localhost:8000/qa/analyze?engine=local" `
  -H "Content-Type: application/json" `
  --data-binary "@input.json"
```

---

## 8. Чек-лист для PG-команды

- [ ] DTO `LlmReviewAspect` (или эквивалент) умеет парсить русский enum sentiment-а (`позитивный`/`негативный`/`нейтральный`).
- [ ] Колонка `review_id` в БД совпадает по типу с input (int64); парсер `output_reviews.json` сохраняет тип, не строкует.
- [ ] Парсер `output_summary.json` обновлён под новую структуру: `recommendations_count` + `summary` + `full_recommendations[]`.
- [ ] PDF-отчёт собирается из `summary` + цикла по `full_recommendations[]`, а не из старого поля `recommendation` (его больше нет).
- [ ] Считает `summary_stats` (sentiment_distribution / top_topics) сам из `output_reviews.json`, если они нужны на UI.
- [ ] Healthcheck бьёт по `http://llm-service:8000/health/live` (внутренний DNS docker-compose).
- [ ] Status-полл для UI бьёт по `http://llm-service:8000/status/{analysis_job_id}`.
