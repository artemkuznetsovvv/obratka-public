# ProxiesController — управление пулом прокси

REST-эндпоинты для добавления, исключения и управления состоянием прокси в БД (`ProxyEntity`). Используются ротатором `DbProxyRotator` при выдаче прокси плагинам.

- Базовый путь: `/api/proxies`
- Контроллер: [src/ParserService/Api/ProxiesController.cs](src/ParserService/Api/ProxiesController.cs)
- Авторизация: атрибут `[RequireQaApiKey]` — заголовок `X-Api-Key: <Qa:ApiKey>` ([src/ParserService/Api/RequireQaApiKeyAttribute.cs](src/ParserService/Api/RequireQaApiKeyAttribute.cs)). В Development-окружении проверка отключена.
- Хост в примерах: `https://parser.193.233.217.223.sslip.io`.

---

## 1. Список прокси — `GET /api/proxies`

Получить все прокси из БД. Опциональный фильтр `enabled_only=true` оставляет только активные (`Enabled=true`).

Ответ: `ProxyListResponse { total, items: ProxyDto[] }`. Пароль наружу не отдаётся.

```bash
# все прокси
curl -X GET "https://parser.193.233.217.223.sslip.io/api/proxies" \
  -H "X-Api-Key: <QA_API_KEY>"

# только включённые
curl -X GET "https://parser.193.233.217.223.sslip.io/api/proxies?enabled_only=true" \
  -H "X-Api-Key: <QA_API_KEY>"
```

---

## 2. Добавить прокси — `POST /api/proxies`

Создаёт новую запись. Уникальный ключ — `(host, port, username)`.

Поля тела (`CreateProxyRequest`):

| Поле       | Тип    | Обязательное | Примечание |
|------------|--------|--------------|-----------|
| `host`     | string | да           | непустой |
| `port`     | int    | да           | 1..65535 |
| `protocol` | string | да           | `http` / `https` / `socks5` (нормализуется через `DbProxyRotator.NormalizeProtocol`) |
| `username` | string | нет          | |
| `password` | string | нет          | |
| `notes`    | string | нет          | произвольная пометка |
| `enabled`  | bool   | нет          | по умолчанию `true` |

Коды:
- `201 Created` — `ProxyDto`
- `400 Bad Request` — невалидный host/port/protocol
- `409 Conflict` — дубль по `(host, port, username)`

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/proxies" \
  -H "X-Api-Key: <QA_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "host": "192.0.2.10",
    "port": 8080,
    "protocol": "http",
    "username": "user1",
    "password": "secret",
    "notes": "RU mobile, MTS",
    "enabled": true
  }'
```

---

## 3. Удалить прокси — `POST /api/proxies/delete`

Физическое удаление записи из БД. Тело: `{ "id": <int> }`.

Коды: `204 No Content` / `404 Not Found`.

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/proxies/delete" \
  -H "X-Api-Key: <QA_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{ "id": 42 }'
```

---

## 4. Исключить из ротации — `POST /api/proxies/disable`

Мягкое отключение: `Enabled=false`. Запись остаётся в БД, ротатор её не выдаёт. Возвращает обновлённый `ProxyDto`.

Коды: `200 OK` / `404 Not Found`.

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/proxies/disable" \
  -H "X-Api-Key: <QA_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{ "id": 42 }'
```

---

## 5. Вернуть в ротацию — `POST /api/proxies/enable`

Включает прокси обратно: `Enabled=true`. Возвращает обновлённый `ProxyDto`.

Коды: `200 OK` / `404 Not Found`.

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/proxies/enable" \
  -H "X-Api-Key: <QA_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{ "id": 42 }'
```

---

## 6. Сбросить health-метрики — `POST /api/proxies/reset-health`

Обнуляет `FailureCount` и `CooldownUntil`. Применяется после ручной проверки прокси, временно «заштрафованного» ротатором (например, после серии сетевых ошибок).

Коды: `200 OK` / `404 Not Found`.

```bash
curl -X POST "https://parser.193.233.217.223.sslip.io/api/proxies/reset-health" \
  -H "X-Api-Key: <QA_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{ "id": 42 }'
```

---

## Замечания

- **Изменения полей не реализовано.** Чтобы поменять `host`/`port`/`username`/`password`/`protocol`/`notes` существующей записи — `delete` + `create`. Через API меняются только `Enabled` (disable/enable) и health-счётчики (reset-health).
- `ProxyDto` отдаёт: `id, host, port, protocol, username, enabled, failureCount, cooldownUntil, lastUsedAt, notes, createdAt, updatedAt` ([src/ParserService/Api/Contracts/ProxyDtos.cs:14-26](src/ParserService/Api/Contracts/ProxyDtos.cs#L14-L26)).
- В PowerShell для одинарных кавычек используйте `--%` или экранируйте кавычки: `curl.exe -X POST ... -d "{\"id\": 42}"`.
