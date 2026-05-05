# Flow парсинга отзывов: search → collection-task → status

Документация end-to-end сценария: ищем компанию по трём источникам, выбираем нужные филиалы, запускаем сбор отзывов и отслеживаем статус.

- Контроллер: [src/ParserService/Api/CollectionTasksController.cs](src/ParserService/Api/CollectionTasksController.cs)
- Базовый путь: `/api/collection-tasks`
- Хост в примерах: `https://parser.193.233.217.223.sslip.io`
- Авторизация: основные эндпоинты (`search`, `POST /`, статусы) — публичные. QA-эндпоинты `/qa/*` требуют `X-Api-Key`.
- Slug источников ([Core/Models/SourceType.cs](src/ParserService/Core/Models/SourceType.cs)): `2gis`, `yandex`, `google`, `otzovik`.

---

## Шаг 1. Поиск компании по источникам — `POST /api/collection-tasks/search`

Ищем компанию (например «Артель») по списку источников и получаем `external_id` для каждого найденного филиала. Один запрос — все источники сразу.

Тело — `SearchRequest`:

| Поле      | Тип        | Описание |
|-----------|------------|----------|
| `query`   | string     | Поисковая строка («Артель») |
| `city`    | string?    | Город для уточнения (опционально) |
| `sources` | string[]   | Slug-и источников: `["2gis","yandex","google"]` |

Ответ — `SearchResponse { results: SearchBranchResultDto[] }`. Каждый результат:

| Поле          | Тип     | Описание |
|---------------|---------|----------|
| `source`      | string  | slug источника, в котором найден филиал |
| `externalId`  | string  | **ключевое поле для запуска сбора** — id организации/филиала в источнике |
| `externalUrl` | string  | URL карточки в источнике |
| `name`        | string  | название |
| `address`     | string  | адрес |
| `rating`      | double? | средний рейтинг |
| `reviewCount` | int?    | известное число отзывов |

`externalId` зависит от источника:
- `yandex` — `businessId` (например `"1124715036"`)
- `2gis` — `firmId`
- `google` — feature id / cid

### curl

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/collection-tasks/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Артель",
    "city": "Москва",
    "sources": ["2gis", "yandex", "google"]
  }'
```

### Пример ответа

```json
{
  "results": [
    {
      "source": "yandex",
      "externalId": "1124715036",
      "externalUrl": "https://yandex.ru/maps/org/artel/1124715036/",
      "name": "Артель",
      "address": "Москва, ул. Тверская, 12",
      "rating": 4.6,
      "reviewCount": 312
    },
    {
      "source": "2gis",
      "externalId": "70000001042201958",
      "externalUrl": "https://2gis.ru/moscow/firm/70000001042201958",
      "name": "Артель",
      "address": "Москва, ул. Тверская, 12",
      "rating": 4.7,
      "reviewCount": 198
    },
    {
      "source": "google",
      "externalId": "ChIJN1t_tDeuEmsRUsoyG83frY4",
      "externalUrl": "https://maps.google.com/?cid=...",
      "name": "Артель",
      "address": "12 Tverskaya St, Moscow",
      "rating": 4.5,
      "reviewCount": 87
    }
  ]
}
```

Дальше отбираем нужные `(source, externalId, externalUrl)` — на их основе запускаем задачи.

---

## Шаг 2. Запуск сбора отзывов — `POST /api/collection-tasks`

**Важно:** один task = один источник. Чтобы собрать «Артель» из 3 источников — запускаем 3 параллельные задачи.

Тело — `CreateCollectionTaskRequest`:

| Поле        | Тип               | Обязательно | Описание |
|-------------|-------------------|-------------|----------|
| `jobId`     | Guid              | да          | id внешней «джобы» (объединяет связанные таски) |
| `companyId` | Guid              | да          | id компании в твоей системе |
| `source`    | string            | да          | slug источника (`yandex`/`2gis`/`google`) |
| `dateFrom`  | DateTimeOffset?   | нет         | инкрементальный сбор: только отзывы новее этой даты |
| `dateTo`    | DateTimeOffset?   | нет         | по умолчанию — `UtcNow` |
| `branches`  | BranchTargetDto[] | да, ≥1      | список филиалов из шага 1 |

`BranchTargetDto`:

| Поле          | Тип    | Описание |
|---------------|--------|----------|
| `branchId`    | Guid   | твой id филиала (вернётся в результате — связать строки отзывов) |
| `externalId`  | string | из `SearchBranchResultDto.externalId` |
| `externalUrl` | string | из `SearchBranchResultDto.externalUrl` |

Ответ: `202 Accepted` + `{ "taskId": "<guid>" }`. Сбор запускается асинхронно в background.

Коды:
- `202 Accepted` — задача создана
- `400 Bad Request` — неизвестный `source` или пустой `branches`

### curl — три источника параллельно

Запускаем три задачи на одну компанию. Каждый запрос отдаёт свой `taskId`.

**Yandex:**
```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/collection-tasks" \
  -H "Content-Type: application/json" \
  -d '{
    "jobId": "11111111-1111-1111-1111-111111111111",
    "companyId": "22222222-2222-2222-2222-222222222222",
    "source": "yandex",
    "dateFrom": "2025-01-01T00:00:00Z",
    "branches": [
      {
        "branchId": "33333333-3333-3333-3333-333333333333",
        "externalId": "1124715036",
        "externalUrl": "https://yandex.ru/maps/org/artel/1124715036/"
      }
    ]
  }'
```

**2GIS:**
```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/collection-tasks" \
  -H "Content-Type: application/json" \
  -d '{
    "jobId": "11111111-1111-1111-1111-111111111111",
    "companyId": "22222222-2222-2222-2222-222222222222",
    "source": "2gis",
    "branches": [
      {
        "branchId": "44444444-4444-4444-4444-444444444444",
        "externalId": "70000001042201958",
        "externalUrl": "https://2gis.ru/moscow/firm/70000001042201958"
      }
    ]
  }'
```

**Google:**
```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/collection-tasks" \
  -H "Content-Type: application/json" \
  -d '{
    "jobId": "11111111-1111-1111-1111-111111111111",
    "companyId": "22222222-2222-2222-2222-222222222222",
    "source": "google",
    "branches": [
      {
        "branchId": "55555555-5555-5555-5555-555555555555",
        "externalId": "ChIJN1t_tDeuEmsRUsoyG83frY4",
        "externalUrl": "https://maps.google.com/?cid=..."
      }
    ]
  }'
```

> Один task поддерживает несколько `branches` — например, у «Артель» 5 филиалов в Яндексе → один task с 5 элементами в `branches`. Прогресс обновляется после каждого филиала.

---

## Шаг 3. Статус задачи — `GET /api/collection-tasks/{taskId}`

Поллинг статуса конкретной задачи. Возвращает `CollectionTaskStatusResponse`:

| Поле          | Тип     | Описание |
|---------------|---------|----------|
| `taskId`      | Guid    | id задачи |
| `status`      | string  | `pending` / `running` / `completed` / `failed` |
| `source`      | string  | slug источника |
| `progress`    | double  | 0.0..1.0 — доля обработанных филиалов |
| `reviewCount` | int?    | итоговое число отзывов (после `completed`) |
| `s3Url`       | string? | ссылка на JSON-результат в S3/MinIO (после `completed`) |
| `error`       | string? | текст исключения (при `failed`) |

```bash
curl -X GET "https://parser.193.233.217.223.sslip.io/api/collection-tasks/<TASK_ID>"
```

### Жизненный цикл

```
pending → running → completed   (нормальный путь, s3Url + reviewCount заполнены)
                  ↘ failed       (error заполнен, частичных результатов в S3 нет)
```

`progress` обновляется по мере прохождения по списку `branches`. `s3Url` появляется только при `completed`.

---

## Шаг 4. Список задач — `GET /api/collection-tasks`

Полезно для дашборда / массового мониторинга «джобы». Query-параметры:

| Параметр | Тип    | Описание |
|----------|--------|----------|
| `status` | string | фильтр: `pending` / `running` / `completed` / `failed` |
| `source` | string | фильтр по slug-у |
| `limit`  | int    | по умолчанию 50, макс 500 |
| `offset` | int    | пагинация |

Ответ: `CollectionTaskListResponse { count, limit, offset, items: CollectionTaskListItem[] }`. Каждый item включает `jobId` и `companyId` — можно отфильтровать клиентом по `jobId`, чтобы собрать все три задачи одной «джобы».

```bash
# все running
curl -X GET "https://parser.193.233.217.223.sslip.io/api/collection-tasks?status=running"

# только yandex, пагинация
curl -X GET "https://parser.193.233.217.223.sslip.io/api/collection-tasks?source=yandex&limit=20&offset=0"

# завершённые
curl -X GET "https://parser.193.233.217.223.sslip.io/api/collection-tasks?status=completed"
```

---

## Сводный сценарий «Артель»

1. **Search** → `POST /api/collection-tasks/search` с `query="Артель"` и тремя источниками. Получаем 3 (или больше) `externalId` — по одному на источник.
2. **Выбор** — клиент решает, какие из найденных филиалов реально нужны (например, фильтрует по адресу).
3. **Старт сбора** — для каждого выбранного источника `POST /api/collection-tasks` с `branches` = выбранные филиалы того источника. На выходе три `taskId` с одним общим `jobId`.
4. **Поллинг** — клиент циклически дёргает `GET /api/collection-tasks/{taskId}` для каждого id (или `GET /api/collection-tasks?status=running` массово). Минимум раз в несколько секунд.
5. **Готово** — когда все три статуса `completed`, забираем JSON из `s3Url` каждого таска. Если `failed` — смотрим `error`.

```
                    ┌──> task #1 (yandex)  ─┐
search "Артель"  ──>│    task #2 (2gis)    ─┼──> 3× s3Url
                    └──> task #3 (google)  ─┘
        ↑                       ↑
   1 запрос               poll status
```

---

## Замечания

- **JobId / CompanyId** клиент выбирает сам — сервис их не валидирует, использует как метаданные/группировку. Чтобы три задачи воспринимались как «один сбор по компании», передавай одинаковые `jobId` и `companyId`.
- **Один источник на task.** Если нужны три источника — три отдельных POST. Так оркестратор изолирует сбои (упал yandex — 2gis и google продолжают).
- **Несколько филиалов в одном task.** В пределах одного источника складывай все `branches` в один task — оркестратор пройдёт по ним последовательно и обновит `progress`.
- **`dateFrom` для инкрементального сбора.** Передаёшь — Яндекс листает по `by_time` и останавливается на первой дате старше `dateFrom` (1-2 страницы вместо 600 отзывов). См. CLAUDE.md → «Поддержка `date_from`».
- **Результаты — в S3/MinIO**, а не в HTTP-ответе. Сервис stateless по результатам: SQLite держит только статусы, JSON отзывов всегда читается по `s3Url`.
- **QA-эндпоинты.** `/qa/{source}/{externalId}` (например `GET /api/collection-tasks/qa/yandex/1124715036`) даёт прямой синхронный вызов плагина без создания таска — удобно для разработки. Требует `X-Api-Key`.
