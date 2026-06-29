# Обновление PG на dev-VPS (рутинный rebuild)

> Краткий operational-гайд: «у меня есть локальные изменения, как их выкатить».
> Без миграций схемы, без сноса volume, без первичной настройки.
> Для разовых миграций — `deploy-schema-2.0.md`. Полная инструкция инфры — `deploy-vps.md`.

VPS: `deploy@193.233.217.223` · стэк PG: `~/processing-gateway/` · код: `~/processing-gateway/app/`.

---

## TL;DR

```bash
# 1. локально — rsync кода
cd /c/Users/nordWorkStudy/Desktop/Obratka/Processing-Gateway
rsync -avz --delete \
    --exclude='.git' --exclude='bin' --exclude='obj' --exclude='data' --exclude='tests' \
    --exclude='docker-compose.yml' --exclude='docker-compose.full.yml' \
    --exclude='docker-compose.with-parser.yml' --exclude='docker-compose.with-llm-pipeline.yml' \
    --exclude='parser-config' --exclude='/*.md' \
    ./ deploy@193.233.217.223:~/processing-gateway/app/

# 2. на VPS — пересобрать только processing-gateway, остальное не трогать
ssh deploy@193.233.217.223 'cd ~/processing-gateway && docker compose up -d --build processing-gateway'

# 3. проверить логи
ssh deploy@193.233.217.223 'cd ~/processing-gateway && docker compose logs --tail=50 processing-gateway'
```

Downtime: ~20–40 секунд, пока контейнер пересоздаётся. Данные в `processing-db` сохраняются.

---

## Когда подходит этот сценарий

Только когда **в патче нет миграций EF Core**:

```bash
# локально, перед rsync
ls src/ProcessingGateway/Infrastructure/Database/Migrations/ | sort
```

Список должен быть тот же, что и на VPS:

```bash
ssh deploy@193.233.217.223 'ls ~/processing-gateway/app/src/ProcessingGateway/Infrastructure/Database/Migrations/'
```

Если добавились новые миграции — они применятся автоматически на старте контейнера (`Database.MigrateAsync()` в [Program.cs:172](src/ProcessingGateway/Program.cs#L172)). Если миграция не-аддитивная (drop колонки, переименование) — иди по сценарию из `deploy-schema-2.0.md`, а не отсюда.

---

## Шаги подробно

### 1. Префлайт локально

```bash
cd /c/Users/nordWorkStudy/Desktop/Obratka/Processing-Gateway

# нет ли несобирающегося кода
dotnet build src/ProcessingGateway/ProcessingGateway.csproj -nologo -v quiet

# нет ли несохранённых изменений в важных файлах
git status
```

Закоммитить изменения **до** rsync — чтобы был anchor для отката.

### 2. rsync кода

```bash
rsync -avz --delete \
    --exclude='.git' \
    --exclude='bin' --exclude='obj' \
    --exclude='data' \
    --exclude='tests' \
    --exclude='docker-compose.yml' \
    --exclude='docker-compose.full.yml' \
    --exclude='docker-compose.with-parser.yml' \
    --exclude='docker-compose.with-llm-pipeline.yml' \
    --exclude='parser-config' \
    --exclude='/*.md' \
    ./ deploy@193.233.217.223:~/processing-gateway/app/
```

Что и почему исключаем:
- `bin/obj` — артефакты локальной сборки, .NET пересоберёт всё внутри контейнера.
- `docker-compose*.yml` — на VPS свой compose, перетирать его нельзя.
- `parser-config` — конфиг Parser-а, к PG отношения не имеет.
- `/*.md` — документация, на VPS не нужна; ведущий `/` ограничивает только корневые `.md` (внутри `src/` markdown по-прежнему синкается, если будет).
- `tests` — на VPS не запускаются.

### 3. Rebuild только PG-сервиса

```bash
ssh deploy@193.233.217.223
cd ~/processing-gateway

# Только processing-gateway — processing-db, llm-pipeline, прочие не трогаем.
docker compose up -d --build processing-gateway
```

`up -d --build <service>` собирает образ заново и пересоздаёт **только** указанный контейнер. Никаких `down`, никаких `-v` — БД-volume в безопасности.

### 4. Проверка

```bash
# Логи запуска
docker compose logs --tail=80 processing-gateway

# Хочется увидеть последовательно:
#   EF migrations applied
#   ParserPoller started: interval=00:00:04, taskTimeout=01:30:00
#   AutoFinalize hosted service started
#   ProcessingGateway started, environment=Production
#   Now listening on: http://[::]:8080

# Live-проверка
docker compose exec processing-gateway sh -c 'wget -qO- http://localhost:8080/health/live'
# {"status":"alive"}

# Внутренний QA-смоук (если есть Gateway:ApiKey)
GATEWAY_API_KEY=$(grep ^GATEWAY_API_KEY ~/processing-gateway/.env | cut -d= -f2)
curl -sS 'https://gateway-dev.193.233.217.223.sslip.io/api/qa/health/dependencies' \
  -H "X-Api-Key: $GATEWAY_API_KEY"
# postgres/rabbitmq/s3/parser — все "ok"
```

### 5. Прогон того, что меняли

Точечно проверь то, что добавил патч. Например для текущего изменения `restart-source` (разрешён рестарт из `completed`):

```bash
# найди completed-job и попробуй рестартовать один источник
docker compose exec processing-db psql -U processing_user -d processing -c \
  "SELECT id, status FROM analysis_jobs WHERE status='completed' ORDER BY created_at DESC LIMIT 1;"

JOB_ID=<id из выдачи>
curl -sS -X POST "https://gateway-dev.193.233.217.223.sslip.io/api/qa/parser/restart-source/$JOB_ID/yandex" \
  -H 'Content-Type: application/json' \
  -H "X-Api-Key: $GATEWAY_API_KEY" \
  -d '{"branches":[{"branchId":"22222222-2222-2222-2222-222222222222","externalId":"1124715036","externalUrl":"https://yandex.ru/maps/org/.../1124715036/"}]}'
# → 202 { ..., "previous_status": "completed", "current_status": "collecting" }
```

---

## Откат

Если новая версия сломалась — два варианта.

### Быстрый: rsync предыдущего коммита

```bash
# локально
git log --oneline -5         # найди хороший коммит, напр. <PREV_SHA>
git stash                    # если есть несохранённое
git checkout <PREV_SHA> -- src/
# повтори §2 rsync
git checkout main -- src/    # вернись на main
git stash pop                # верни рабочие изменения
```

Потом на VPS — снова `docker compose up -d --build processing-gateway`.

### Если упала миграция

Скорее всего ты ошибся со сценарием — этот гайд для рутинных rebuild'ов. Иди по `deploy-schema-2.0.md` §«Откат» или восстанавливай из бэкапа `~/backups/`.

---

## Что **не** покрыто

- Миграции схемы БД с потерей данных — `deploy-schema-2.0.md`.
- Первичная установка стэка / настройка vhost / certbot — `deploy-vps.md`.
- Параллельное обновление `parser-service` или `llm-pipeline` — у них свои репо и свой rsync-флоу.
- Production prod-стенд (blue-green, миграция данных) — на MVP не нужно.
