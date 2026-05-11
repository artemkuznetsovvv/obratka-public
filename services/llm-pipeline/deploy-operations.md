# Эксплуатация LLM Pipeline на VPS

Операционная памятка: как выкатывать обновления Python-воркера с локальной машины,
управлять `OPENROUTER_API_KEY` и моделями, чистить persistent state.
Базовое развёртывание (заказ VPS, ufw, docker, PG-стек) описано в
`Parser-Service/deploy-vps.md` и `Processing-Gateway/deploy-vps.md` — этот файл
предполагает, что **PG-стек уже поднят**, docker-сеть `parser-service_internal`
существует, а контейнер `obratka-llm-worker` хотя бы раз стартовал.

VPS: `193.233.217.223`, пользователь `deploy`.

**Раскладка директорий на VPS:**

| Путь | Что |
|------|-----|
| `~/llm-pipeline/app/` | Python-код (`src/`, `Dockerfile.worker`, `pyproject.toml`, `poetry.lock`, `README.md`) — сюда синкаем rsync-ом |
| `~/llm-pipeline/.env` | секреты (`OPENROUTER_API_KEY` + RABBIT/S3 creds), монтируется `env_file:` |
| `~/llm-pipeline/data/` | bind-mount `/app/data` — `job_state.sqlite` для `/status/{jobId}` |
| `~/llm-pipeline/logs/` | bind-mount `/app/logs` — логи loguru |
| `~/processing-gateway/docker-compose.yml` | **единственный** prod-compose; включает сервис `llm-pipeline` с `build.context: ../llm-pipeline/app` |

Компонует и запускает сервис всегда `~/processing-gateway/docker compose ...` — там
описан внешний `parser-service_internal` (из Parser-стэка), туда же подключён LLM-pipeline.

> **Важно — где живёт сервис.** В отличие от Parser-а, LLM Pipeline **наружу не
> выставляется**: ни через nginx, ни через прямой port-mapping. Доступ к нему
> только из docker-сети `parser-service_internal` — PG читает `/status/{jobId}`
> по docker-DNS `http://llm-pipeline:8000`, сообщения принимает через
> RabbitMQ `llm.requests`. Поэтому секции «allowlist» / «выдать доступ коллеге»
> в этой памятке нет — управление доступом сведено к **secrets** (OPENROUTER,
> RABBIT, S3) и сетевой изоляции docker.
>
> **Локальный dev-сценарий** (`llm_pipline/docker-compose.yml` или PG-override
> `docker-compose.with-llm-pipeline.yml`) — поверх dev-стэка PG, на VPS **не используется**.

---

## 1. Обновление кода через rsync

Используется, когда правишь Python-код у себя и хочешь выкатить на стенд без git.
rsync должен быть установлен на обеих сторонах (`sudo apt install rsync`).

### 1.1. Пересинк исходников

На **локальной машине** (WSL Ubuntu). **Цель — `~/llm-pipeline/app/`** (не корень `~/llm-pipeline/`),
потому что в VPS-compose `build.context: ../llm-pipeline/app` — Dockerfile и `src/` ищутся
именно там, а `~/llm-pipeline/.env`, `~/llm-pipeline/data/`, `~/llm-pipeline/logs/` живут
**рядом** с `app/` (соседями), а не внутри.

```bash
cd /mnt/c/Users/nordWorkStudy/Desktop/Obratka/llm_pipline    # актуальный путь к репо

rsync -avz --delete \
    --exclude='.git' --exclude='.venv' --exclude='.idea' --exclude='.vscode' \
    --exclude='__pycache__' --exclude='*.pyc' --exclude='*.pyo' \
    --exclude='.pytest_cache' --exclude='.mypy_cache' --exclude='.ruff_cache' \
    --exclude='qa_outputs' --exclude='out' --exclude='logs' --exclude='data' \
    --exclude='build.log' --exclude='llm_response.json' --exclude='input*.json' \
    --exclude='.env' --exclude='.env.worker' \
    --exclude='/docker-compose.yml' \
    --include='/README.md' --exclude='/CLAUDE.md' --exclude='/*.md' \
    --exclude='tasks' \
    ./ deploy@193.233.217.223:~/llm-pipeline/app/
```

> **Важно про порядок `--include`/`--exclude` для `.md`-файлов:** rsync применяет фильтры
> по принципу «первое совпадение побеждает». `--include='/README.md'` должен идти
> **перед** `--exclude='/*.md'`, иначе README отлетит вместе с остальными markdown'ами.
> README **обязателен** для билда — в `pyproject.toml` он объявлен как `readme = "README.md"`,
> и `poetry install` без него падает на шаге `COPY README.md`.

Что значат флаги:
- `-a` — archive: рекурсивно, сохраняет права, симлинки, timestamps
- `-v` — verbose: показывает что синкается
- `-z` — сжатие на лету (ускоряет по медленному каналу)
- `--delete` — удаляет на VPS то, чего больше нет локально. Важно, если удалили
  модуль из `src/obratka/` — без этого старый `.py` останется в образе. Если
  боитесь затереть что-то на VPS — уберите этот флаг.

Почему исключаем:
- `.venv`, `__pycache__`, `*.pyc`, `.pytest_cache`, `.ruff_cache`, `.mypy_cache`
  — артефакты Python; в контейнере Poetry поставит зависимости заново
- `qa_outputs`, `out`, `logs`, `data` — runtime-артефакты, в проде они в docker-volumes
  (`llm-pipeline-state`, `llm-pipeline-logs`) и в bind-mount `qa_outputs/`. Перезатирать
  бессмысленно и опасно — `data/job_state.sqlite` хранит `/status` по job-ам
- `.env` — там реальный `OPENROUTER_API_KEY`. **Никогда** не синкаем секреты с
  локальной машины: на VPS живёт свой `.env`, который писали при первом деплое
- `/docker-compose.yml` — корневой dev-compose (QA-mode standalone); на VPS
  работает прод-compose из `~/processing-gateway/` (override
  `docker-compose.with-llm-pipeline.yml`). Ведущий `/` — «только в корне»
- `CLAUDE.md`, `/*.md`, `tasks/` — инструкции для Claude и ТЗ модулей, в проде не нужны
- `build.log`, `llm_response.json`, `input*.json` — локальные дампы, не нужны на VPS

### 1.2. Пересборка на VPS

LLM-pipeline объявлен сервисом `llm-pipeline` в **прод-compose Processing Gateway**
(`~/processing-gateway/docker-compose.yml`), с `build.context: ../llm-pipeline/app`.
Соответственно команды запускаем из `~/processing-gateway/`:

```bash
ssh deploy@193.233.217.223
cd ~/processing-gateway

docker compose build llm-pipeline
docker compose up -d llm-pipeline

docker compose logs -f llm-pipeline    # Ctrl+C когда увидели
                                        # "LLM service listening on llm.requests"
                                        # + "Uvicorn running on http://0.0.0.0:8000"
```

`docker compose up -d llm-pipeline` пересоздаёт **только** контейнер `llm-pipeline`.
RabbitMQ, MinIO, Postgres, Seq, PG-worker — не трогаются. SQLite job-state
(`~/llm-pipeline/data/job_state.sqlite`) переживает рестарт, потому что лежит в
bind-mount на хосте.

> **Не используй** dev-compose-override `docker-compose.with-llm-pipeline.yml`
> или `docker-compose.full.yml` — на VPS их нет, они для локалки.

### 1.3. Когда нужен `--no-cache`

Слои Dockerfile с `apt-get install`, `pip install langdetect`, `poetry install` кешируются
агрессивно. Принудительно пересобрать всё:

```bash
cd ~/processing-gateway
docker compose build --no-cache llm-pipeline
```

Кейсы когда нужно:
- Смена версии Python в `Dockerfile.worker` (FROM python:3.12-slim → 3.13)
- Изменения в `pyproject.toml` / `poetry.lock` (добавили/удалили/обновили зависимости).
  Обычный билд это **должен** подхватить через инвалидацию слоя `COPY pyproject.toml`,
  но если poetry.lock не обновился локально (например, sync'нули только `pyproject.toml`)
  — кеш слоя `poetry install` останется старым
- Обновление зависимостей через `apt` (новый системный пакет в `RUN apt-get install`)
- Подозрение на «образ собрался, но работает старый код» — например, `langdetect`
  тянется отдельным `pip install` до poetry, и его кеш отдельный

### 1.4. Что делать если менялся только конфиг, а не код

Все команды — из `~/processing-gateway/`.

| Что изменилось | Команда |
|----------------|---------|
| `~/processing-gateway/docker-compose.yml` (блок `llm-pipeline:`) | `docker compose up -d llm-pipeline` (применит diff без ребилда) |
| `~/llm-pipeline/.env` (OPENROUTER, model overrides, LOG_LEVEL) | `docker compose up -d llm-pipeline` (пересоздаст контейнер с новыми env). `restart` **не** подхватит изменения `env_file:` — нужен именно `up -d` |
| Промпты / модели в `src/obratka/config.py` | это код, нужен билд из §1.2 |

### 1.5. Rollback

Перед выкаткой рисковой фичи — снимите снапшот текущего образа:

```bash
docker tag obratka-llm-worker:latest obratka-llm-worker:backup-$(date +%F)
```

Если новая версия отвалилась (например, новая модель OpenRouter возвращает не тот формат
и instructor роняет retry-петлю) — откат:

```bash
docker tag obratka-llm-worker:backup-2026-05-11 obratka-llm-worker:latest
cd ~/processing-gateway
docker compose up -d --force-recreate llm-pipeline
```

Плюс `git` локально — всегда можно `git checkout <commit>` + `rsync` старую версию кода.

**Внимание:** при откате `~/llm-pipeline/data/job_state.sqlite` **не** откатывается —
это bind-mount на хосте, живёт независимо от образа. Если новая версия успела
записать туда несовместимый формат — придётся почистить (см. §3.3).

### 1.6. Чек-лист после выкатки

Все команды — из `~/processing-gateway/`. Сервис в compose называется `llm-pipeline`
(имя контейнера может отличаться — посмотри через `docker compose ps llm-pipeline`).

```bash
# 1. Контейнер Up / healthy
docker compose ps llm-pipeline

# 2. На старте нет exception, AMQP подписался, HTTP стартанул
docker compose logs --tail=50 llm-pipeline
# Ожидание:
#   - "LLM service listening on llm.requests"
#   - "Uvicorn running on http://0.0.0.0:8000"
#   - НЕТ "OPENROUTER_API_KEY is not set" / "AMQPConnectionError"

# 3. REST /health отвечает (изнутри docker-сети — снаружи он не выставлен!)
docker compose exec llm-pipeline \
    python -c "import urllib.request; print(urllib.request.urlopen('http://localhost:8000/health/live').read())"
# Ожидание: b'{"status":"alive"}'

# 4. Очередь llm.requests реально имеет подписчика (RabbitMQ живёт в parser-стэке)
cd ~/parser-service && docker compose exec rabbitmq rabbitmqctl list_queues name consumers messages_ready
cd ~/processing-gateway
# Ожидание: llm.requests   1   0   (consumers=1 — значит worker подписался)

# 5. /status для несуществующего job-а отдаёт 404 (а не 500)
docker compose exec llm-pipeline \
    python -c "import urllib.request, urllib.error; \
try: print(urllib.request.urlopen('http://localhost:8000/status/00000000-0000-0000-0000-000000000000').read())
except urllib.error.HTTPError as e: print(e.code, e.read())"
# Ожидание: 404 b'{"detail":{"analysis_job_id":"...","status":"unknown"}}'
```

Если контейнер в `Restarting` — `docker compose logs llm-pipeline` покажет причину
(чаще всего: пустой `OPENROUTER_API_KEY` или недоступный RabbitMQ — см. §5).

---

## 2. Управление OPENROUTER_API_KEY и моделями

LLM-pipeline ходит во **внешний** API OpenRouter, и весь биллинг — на стороне ключа.
Это **главный** секрет сервиса; если он утёк или закончились деньги — pipeline встаёт.

`OPENROUTER_API_KEY` живёт в `~/llm-pipeline/.env` на VPS и **никогда** не должен попадать
в репо, образ Docker или логи.

### 2.1. Где смотреть текущий ключ

```bash
ssh deploy@193.233.217.223
sudo cat ~/llm-pipeline/.env | grep OPENROUTER
# OPENROUTER_API_KEY=sk-or-v1-...
```

Внутри контейнера он же доступен как env (но не печатать в логи — `loguru` маскирует
`*_KEY` / `*_SECRET` только если разработчик добавил sink-фильтр; на проде лучше просто
не логировать секреты явно).

### 2.2. Ротация ключа

На сайте OpenRouter (https://openrouter.ai/keys) создать **новый** ключ, потом:

```bash
ssh deploy@193.233.217.223
nano ~/llm-pipeline/.env
# Заменить строку OPENROUTER_API_KEY=sk-or-v1-...

cd ~/processing-gateway
docker compose up -d llm-pipeline       # ВАЖНО: именно up -d, не restart —
                                         # env_file перечитывается только при
                                         # пересоздании контейнера

docker compose logs -f llm-pipeline | head -30
# Должно быть чисто, без "401 Unauthorized" / "Invalid API key"
```

После того как новый ключ заработал — старый **удалить** в панели OpenRouter
(не «деактивировать», а именно delete). Утёкший ключ — самый частый канал слива
бюджета в LLM-проектах.

### 2.3. Контроль расходов

Бюджет на цикл (см. `docs/pricing.md`):
- 500 отзывов: ~$0.20
- 3 000 отзывов: ~$1.16
- 15 000 отзывов: ~$3.99

На OpenRouter (https://openrouter.ai/credits) задать **лимит** на ключ: например
`$50/мес`. Если pipeline зациклится на retry/instructor-loop — это даст автоматический
стоп, а не «опустошённый счёт за ночь».

Проверить фактический расход за пайплайн — в логах worker'а:

```bash
cd ~/processing-gateway
docker compose logs llm-pipeline | grep -E "cost|tokens" | tail -50
```

(loguru пишет latency + cost per LLM-вызов, см. `tasks/00_logging.md`).

### 2.4. Смена моделей / промптов

Модели зашиты в `src/obratka/config.py` (читаются из `.env`, см. `tasks/11_config_and_env.md`).
По умолчанию:

| Шаг | Модель |
|-----|--------|
| Шаг 0.5 (перевод не-RU) | Gemini 2.0 Flash |
| Шаг 2 (темы + тональность) | GPT-4o-mini |
| Шаг 2.2 (reclassification low-confidence) | GPT-4o |
| Шаг 2.1 (кластеризация свободных тем) | GPT-4o-mini |
| Шаг 4 (рекомендации) | DeepSeek V3.2 |

**Лёгкая смена** (через env, без пересборки) — добавить в `.env` соответствующий override:

```bash
nano ~/llm-pipeline/.env
# Например:
# OBRATKA_MODEL_STEP2=openai/gpt-4o-mini
# OBRATKA_MODEL_STEP4=deepseek/deepseek-chat
```

И `up -d llm-pipeline` (см. §1.4).

**Тяжёлая смена** (новый шаг, новый промпт, изменение схемы Pydantic) — это код,
полный цикл из §1.1–§1.2.

После любой смены модели — прогнать smoke-test через QA-ручку (см. §4.1), чтобы убедиться,
что новая модель возвращает совместимый JSON (instructor + Pydantic перевалидируют).

---

## 3. Управление persistent state

LLM-pipeline хранит данные на хосте через **bind-mount**-ы (а не named-volumes!).
Всё видно из `~/llm-pipeline/` без `docker exec`:

| Данные | На хосте | В контейнере |
|--------|----------|--------------|
| `/status/{jobId}` — состояние job-ов (stage, progress, started_at) | `~/llm-pipeline/data/job_state.sqlite` | `/app/data/job_state.sqlite` |
| Логи loguru (rotating) | `~/llm-pipeline/logs/*.log` | `/app/logs/*.log` |
| QA-выходы (когда `OBRATKA_QA_ENABLED=true`) | `~/llm-pipeline/qa_outputs/<job_id>/` (если добавишь bind в compose) | `/app/qa_outputs/` |

### 3.1. Посмотреть состояние job-ов (SQLite)

Прямо с хоста (если установлен `sqlite3`):

```bash
sqlite3 ~/llm-pipeline/data/job_state.sqlite \
    "SELECT analysis_job_id, status, stage, progress, started_at, finished_at, error \
     FROM job_state ORDER BY started_at DESC LIMIT 20"
```

Если `sqlite3` не установлен — через контейнер:

```bash
cd ~/processing-gateway
docker compose exec llm-pipeline \
    python -c "import sqlite3; \
c = sqlite3.connect('/app/data/job_state.sqlite'); \
c.row_factory = sqlite3.Row; \
rows = c.execute('SELECT analysis_job_id, status, stage, progress, started_at, finished_at, error FROM job_state ORDER BY started_at DESC LIMIT 20').fetchall(); \
[print(dict(r)) for r in rows]"
```

(Точное имя таблицы — проверить в `src/obratka/web/state.py`. Если другая схема — поправить SELECT.)

### 3.2. Посмотреть логи

Прямо с хоста (bind-mount):

```bash
ls -la ~/llm-pipeline/logs/
tail -n 200 ~/llm-pipeline/logs/obratka.log
tail -f ~/llm-pipeline/logs/obratka.log | grep -E "ERROR|WARN"
```

Или через docker (stdout, который пишет uvicorn/loguru):

```bash
cd ~/processing-gateway
docker compose logs --tail=200 llm-pipeline          # последние 200 строк
docker compose logs --since=10m llm-pipeline         # за последние 10 минут
docker compose logs -f llm-pipeline | grep -E "ERROR|WARN"
```

Если хотите централизованные логи — `loguru` пишет в stdout, который docker отдаёт
своим logging-driver. На стэке поднят Seq (`logs.193.233.217.223.sslip.io`), но
**Python-worker туда пока не отправляет** (Seq настроен только под .NET сервисы через
Serilog). Если потребуется — добавить sink `loguru → seq` отдельной задачей.

### 3.3. Почистить job_state (после rollback или несовместимого изменения схемы)

Поскольку это bind-mount — просто удаляем файл на хосте:

```bash
cd ~/processing-gateway
docker compose stop llm-pipeline
rm ~/llm-pipeline/data/job_state.sqlite
docker compose up -d llm-pipeline                  # worker создаст пустую БД при старте
```

После такого `/status/{jobId}` для всех **прошлых** job-ов начнёт возвращать `404 unknown` —
PG это переживает (он считает unknown как «истёк» и переотправляет через QA-replay).

### 3.4. Почистить логи

`loguru` уже делает rotation (см. `tasks/00_logging.md`). Если по какой-то причине
ротация не работает и `~/llm-pipeline/logs/` распух:

```bash
find ~/llm-pipeline/logs -name "*.log*" -mtime +7 -delete
du -sh ~/llm-pipeline/logs
```

(Прямо на хосте — никаких `docker exec` не нужно.)

### 3.5. QA-выходы

В текущем VPS-compose `qa_outputs` bind-mount **не подключён** (комментарий в compose:
«порт 8000 НЕ пробрасываем»). Если хочешь использовать QA-режим — добавь в блок
`llm-pipeline:` строку:

```yaml
volumes:
  - ../llm-pipeline/qa_outputs:/app/qa_outputs
```

После этого:

```bash
ls -la ~/llm-pipeline/qa_outputs/
cat ~/llm-pipeline/qa_outputs/<job_id>/output_summary.json | jq
```

Чистить можно безопасно — это только результаты ручных QA-прогонов, к prod-pipeline
отношения не имеют.

---

## 4. QA-режим (прогон `input.json` в обход AMQP/PG)

LLM-worker поднимает HTTP-ручку `POST /qa/analyze`, которая принимает целиком
`input.json` и прогоняет его через пайплайн **без** RabbitMQ и без S3. Удобно для
диагностики, когда PG ещё на schema 1.0 и e2e не работает, а проверить нужно
именно LLM-логику.

### 4.1. Включить QA-режим

В **VPS-compose** `~/processing-gateway/docker-compose.yml`, в блоке `llm-pipeline:`,
добавить (или раскомментировать) два env-параметра и bind-mount для выходов:

```yaml
services:
  llm-pipeline:
    environment:
      OBRATKA_QA_ENABLED: "true"
      LLM_QA_OUTPUT_DIR: /app/qa_outputs
    volumes:
      - ../llm-pipeline/qa_outputs:/app/qa_outputs     # bind на хост — видеть результат напрямую
```

Применить:

```bash
mkdir -p ~/llm-pipeline/qa_outputs           # создать каталог, чтобы bind не упал
cd ~/processing-gateway
docker compose up -d llm-pipeline
```

### 4.2. Прогон input.json

Ручка `/qa/analyze` доступна **только из docker-сети** (наружу не выставлена).
Прогоняем из контейнера PG (или любого, кто в `parser-service_internal`):

```bash
# Скопировать тестовый input на VPS:
scp ./input.json deploy@193.233.217.223:~/llm-pipeline/

# На VPS — отправить во worker:
ssh deploy@193.233.217.223
cd ~/processing-gateway
docker compose exec processing-gateway curl -sS \
    -X POST http://llm-pipeline:8000/qa/analyze \
    -H "Content-Type: application/json" \
    --data-binary "@-" < ~/llm-pipeline/input.json
```

Результат пишется в `~/llm-pipeline/qa_outputs/<analysis_job_id>/{output_reviews.json,output_summary.json}`.

### 4.3. Когда выключить QA-режим

В проде (после миграции PG на schema 2.0, когда e2e через RabbitMQ заработает) —
убрать из compose `OBRATKA_QA_ENABLED`, `LLM_QA_OUTPUT_DIR` и bind-mount `qa_outputs`,
сделать `docker compose up -d llm-pipeline`. Ручка `/qa/analyze` не имеет аутентификации;
пока сервис изолирован в docker-сети — это безопасно, но если когда-то выставим
status-endpoint наружу через nginx — оставлять `/qa` открытой нельзя.

---

## 5. Частые проблемы

### 5.1. «После rsync код на VPS старый»

Проверьте, что синк реально прошёл (цель — `~/llm-pipeline/app/`, а не корень!):

```bash
# на VPS
ls -la ~/llm-pipeline/app/src/obratka/runner.py
# timestamp должен совпадать с локальным файлом
```

Если timestamp старый — rsync не докинул. Частые причины:
- Цель в rsync написали `~/llm-pipeline/` вместо `~/llm-pipeline/app/` — build тогда
  не найдёт Dockerfile, потому что `context: ../llm-pipeline/app`
- Случайно добавили `*.py` в exclude — проверьте команду из §1.1 буква в букву
- `--delete` убрало что-то нужное локально (например, тестовый файл, который и так
  должен жить только на VPS) — перезапустите без `--delete`
- Билд использует **старый слой** Dockerfile `COPY src /app/src` — пересоберите
  через `--no-cache` (см. §1.3)

### 5.2. «Worker стартует, но через секунду падает — "OPENROUTER_API_KEY is not set"»

`.env` на VPS либо отсутствует, либо в нём нет ключа, либо compose не подхватил
`env_file`:

```bash
ssh deploy@193.233.217.223
sudo cat ~/llm-pipeline/.env | grep OPENROUTER     # должна быть непустая строка
ls -la ~/llm-pipeline/.env                          # права 0600, owner deploy
```

В VPS-compose `env_file:` указано как `../llm-pipeline/.env` (относительно
`~/processing-gateway/`), что разрешается в `~/llm-pipeline/.env`. Если файл там
действительно есть и `up -d` не подхватил — проверь, что в compose именно
`../llm-pipeline/` (с дефисом), а не `../llm_pipline/` (с подчёркиванием — это имя
репо локально, но на VPS папка названа с дефисом).

### 5.3. «Worker стартует, но не подписывается на llm.requests»

Симптом: в `rabbitmqctl list_queues` у `llm.requests` `consumers=0`, в логах
worker'а `aio_pika` пытается коннектиться и падает.

Проверки:

```bash
# 1. RabbitMQ вообще жив (он живёт в parser-стэке)?
cd ~/parser-service && docker compose ps rabbitmq

# 2. Сеть правильная? Контейнер llm-pipeline должен быть в parser-service_internal
docker inspect $(docker compose -f ~/processing-gateway/docker-compose.yml ps -q llm-pipeline) \
    --format '{{json .NetworkSettings.Networks}}' | jq

# 3. Creds в .env совпадают с RABBIT_USER/PASS PG-стэка
cd ~/parser-service && docker compose exec rabbitmq rabbitmqctl authenticate_user gateway gateway_pwd
# Ожидание: Success
```

Если RABBIT_URL в `.env` указывает на `localhost:5672` (как в `.env.worker.example`
для **локальной** разработки), а контейнер живёт в docker-сети — нужен
`rabbitmq:5672`. VPS-compose должен пробрасывать его через `environment:` —
проверь, не перетёрто ли значение в `~/llm-pipeline/.env`.

### 5.4. «/status/{jobId} возвращает 404 для job-а, который точно был обработан»

Возможные причины:
- Файл `~/llm-pipeline/data/job_state.sqlite` удалили (см. §3.3)
- Bind-mount не подключился (compose-блок `volumes:` поломан) — `/status`
  пишется в эфемерный `/app/data/`, который теряется при рестарте контейнера
- Job обрабатывался **другим инстансом** worker'а (в текущей конфигурации инстанс
  один, но если масштабировали — каждый держит свой `job_state.sqlite`)

Проверка bind-mount-а:

```bash
docker inspect $(docker compose -f ~/processing-gateway/docker-compose.yml ps -q llm-pipeline) \
    --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'
# Должно быть видно: /home/deploy/llm-pipeline/data -> /app/data
```

В таком случае реальный результат всё равно есть в S3
(`s3://obratka-jobs/{jobId}/output_*.json`) — это финальная истина. `/status` — только
для UX-прогресса PG в момент работы pipeline.

### 5.5. «OpenRouter возвращает 429 / rate limit»

Pipeline ушёл в retry-петлю на одной модели. Проверки:

```bash
cd ~/processing-gateway
docker compose logs llm-pipeline | grep -E "429|rate" | tail -30
```

Действия:
- Проверить лимит ключа в OpenRouter (https://openrouter.ai/credits)
- Уменьшить конкурентность asyncio-семафора (см. `tasks/01_async_orchestrator.md`,
  переменная вроде `OBRATKA_LLM_CONCURRENCY` в `.env`)
- Сменить модель шага на менее загруженную (см. §2.4)

### 5.6. «PG получает LLM-ответ, но валится на парсинге output_*.json»

Это **schema mismatch**, не баг LLM. На MVP PG-код может быть на schema 1.0,
а LLM публикует 2.0 (см. `llm_contracts_changed.md`).
Действия:
- Сверить версию PG-кода (там должен быть consumer на `output_reviews.json` +
  `output_summary.json`, а не `output.json`)
- Если PG ещё на 1.0 — e2e через PG не запустится. Пользоваться QA-ручкой §4

### 5.7. «llm-stub и llm-pipeline оба слушают llm.requests»

Если в VPS-compose остался сервис `llm-stub` от старой раскладки — он будет
конкурировать с реальным `llm-pipeline` за сообщения из `llm.requests` (round-robin
по consumers'ам), и половина job-ов уйдёт в заглушку.

```bash
cd ~/parser-service
docker compose exec rabbitmq rabbitmqctl list_queues name consumers
# llm.requests должна иметь consumers=1
```

Если consumers=2 — найти и остановить stub:

```bash
cd ~/processing-gateway
docker compose ps | grep stub
docker compose stop llm-stub
# и закомментировать/удалить сервис llm-stub в compose, иначе после рестарта
# он снова поднимется
```

### 5.8. «`docker compose build` падает на `poetry install` — "ImportError LogCmd"»

Известный баг Poetry 1.8.x с `virtualenv` (см. комментарий в `Dockerfile.worker`).
Уже обойдён: `langdetect` ставится отдельным `pip install` до poetry. Если
после смены версии Python / Poetry баг вернулся — посмотрите
https://github.com/python-poetry/poetry/issues/9966.

---

## 6. Полезные алиасы на VPS

Добавьте в `~/.bashrc` чтобы не печатать длинные команды:

```bash
# Базовый префикс — заходим в каталог PG и используем единственный docker-compose.yml
alias llmc='cd ~/processing-gateway && docker compose'

# Часто используемое
alias llmlogs='llmc logs -f --tail=50 llm-pipeline'
alias llmps='llmc ps llm-pipeline'
alias llmup='llmc up -d llm-pipeline'
alias llmbuild='llmc build llm-pipeline && llmc up -d llm-pipeline'
alias llmrebuild='llmc build --no-cache llm-pipeline && llmc up -d --force-recreate llm-pipeline'
alias llmsh='llmc exec llm-pipeline bash'
alias llmhealth='llmc exec llm-pipeline python -c "import urllib.request; print(urllib.request.urlopen(\"http://localhost:8000/health/live\").read())"'
# RabbitMQ живёт в parser-стэке, поэтому отдельный алиас:
alias llmqs='cd ~/parser-service && docker compose exec rabbitmq rabbitmqctl list_queues name consumers messages_ready | grep llm'
```

Применить: `source ~/.bashrc`.

Примеры использования:

```bash
llmbuild        # пересобрать после rsync (обычный билд + перезапуск)
llmlogs         # хвост логов
llmhealth       # проверить /health/live
llmqs           # увидеть llm.requests / llm.results и сколько consumers
llmsh           # зайти внутрь контейнера
```

---

## Связанные документы

- [LLM_PYTHON_QUICKSTART.md](LLM_PYTHON_QUICKSTART.md) — полный контракт с PG (AMQP + S3 + REST)
- [llm_contracts_changed.md](llm_contracts_changed.md) — отличия schema 2.0 от 1.0
- [docs/pricing.md](docs/pricing.md) — расчёт стоимости одного цикла анализа
- [CLAUDE.md](CLAUDE.md) — архитектура и поток данных LLM-пайплайна
- `../Parser-Service/deploy-operations.md` — родственная памятка по Parser
- `../Processing-Gateway/deploy-vps.md` — как PG-стек поднят на VPS (RabbitMQ, MinIO,
  Postgres, Seq), к которому подключается LLM-pipeline
