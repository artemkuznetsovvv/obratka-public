# Развёртывание Processing Gateway на VPS

Пошаговая инструкция: от чистого `~/processing-gateway/` каталога до рабочего стенда,
подключённого к **уже работающей инфраструктуре Parser Service** (`~/parser-service/` —
docker network `parser-service_internal` с MinIO, RabbitMQ, Seq, nginx).

**Целевой стек:**
- ProcessingGateway (.NET 9 worker + минимальный HTTP) — статус-API наружу через nginx
- LlmStub (.NET 9 worker) — внутри docker network, наружу не выставляется
- Postgres 16 — внутри docker network (порт 5432 не пробрасываем; локально для DBeaver используем SSH-туннель)
- **Шарят с Parser-стэком**: MinIO, RabbitMQ, Seq, nginx, certbot (всё в `~/parser-service/`)

**Три слоя защиты** (как у Parser-а):
Aeza Cloud Firewall → ufw → nginx IP allowlist + X-Api-Key для QA-ручек.

> Базовая инфраструктура (заказ VPS, ufw, fail2ban, Docker, пользователь `deploy`,
> certbot, базовый nginx-vhost) — описана в `Parser-Service/deploy-vps.md`, шаги 0–9.
> Этот документ предполагает, что **Parser-стэк уже работает** и docker-сеть
> `parser-service_internal` существует. Если нет — сначала разверни Parser.
>
> **Важно про certbot:** certbot живёт **как docker-контейнер** в parser-стеке
> (`parser-service-certbot-1`), а не как системная утилита. Все certbot-команды ниже —
> через `docker compose run --rm`, а не напрямую `certbot ...`.

VPS: `193.233.217.223`, пользователь `deploy`. Все пути — относительно `/home/deploy/`.

---

## Шаг 0. Что должно быть готово

Проверь на VPS:

```bash
# 1. Parser-стэк работает
ssh deploy@193.233.217.223
cd ~/parser-service
docker compose ps
# Ожидание: parser, minio, rabbitmq, seq, nginx, certbot, minio-init (Exited 0) — все Up.

# 2. Docker network parser-service_internal существует
docker network ls | grep parser-service
# Ожидание: parser-service_internal   bridge   local
#           parser-service_default    bridge   local

# 3. Parser API + Seq UI доступны снаружи (в браузере)
#    https://parser.193.233.217.223.sslip.io   — открывается с whitelisted IP
#    https://logs.193.233.217.223.sslip.io     — открывается с whitelisted IP

# 4. Бакет obratka-jobs создан Parser-а minio-init контейнером — проверять не нужно,
#    он создан при первом старте парсера и виден в MinIO.
```

Если что-то не так — фикси сначала Parser-стэк (см. его `deploy-vps.md`).

---

## Шаг 1. Локальная подготовка (на твоей машине)

### 1.1. Секреты — сгенерировать заранее

Сохрани в **локальный** менеджер паролей (НЕ в репо):

```
PROCESSING_DB_PASSWORD=<openssl rand -hex 24>      # пароль владельца БД
ANALYTICS_READER_PASSWORD=<openssl rand -hex 24>   # пароль read-only роли для Web API
GATEWAY_API_KEY=<openssl rand -hex 32>             # X-Api-Key для QA-ручек
```

Команды для генерации:
```bash
openssl rand -hex 32   # 64 hex chars — для GATEWAY_API_KEY
openssl rand -hex 24   # 48 hex chars — для паролей
```

Также понадобятся **уже существующие** секреты Parser-стэка. Получить:
```bash
ssh deploy@193.233.217.223
sudo cat ~/parser-service/.env
```
Тебе нужны эти ключи (точно те же имена, что в parser-`.env`):
- `MINIO_ACCESS_KEY`, `MINIO_SECRET_KEY` — root-credentials MinIO. PG ходит в MinIO с теми же ключами, что Parser (на MVP оба равноправны).
- `RABBIT_USER`, `RABBIT_PASS` — RabbitMQ-credentials. На MVP PG ходит под этим же пользователем; в будущем выделим `gateway` отдельно (см. §11).
- `SEQ_INGESTION_KEY` — общий или **новый** ключ Seq (мы создадим отдельные ключи `processing-gateway` и `llm-stub` в §7).

Их **не нужно** переписывать в Parser-стэк — мы их перепишем в `~/processing-gateway/.env`.

### 1.2. DNS — ничего регистрировать не нужно

Используем сервис **sslip.io** — wildcard DNS, который резолвит любой поддомен по правилу
`<что-угодно>.<IP>.sslip.io → <IP>` без регистрации, без панели управления.

Целевые поддомены:
- `gateway-dev.193.233.217.223.sslip.io` → 193.233.217.223 (PG QA-API, §8)
- `s3-admin.193.233.217.223.sslip.io` → 193.233.217.223 (MinIO Web Console, §8.5, опц.)

Проверка, что DNS работает (выполни на локальной машине):
```bash
nslookup gateway-dev.193.233.217.223.sslip.io
# или
ping gateway-dev.193.233.217.223.sslip.io
# Должно резолвиться в 193.233.217.223 — без любой настройки.
```

> Так же работают уже существующие `parser.193.233.217.223.sslip.io` и
> `logs.193.233.217.223.sslip.io` Parser-стэка — A-записи для них не регистрировались.

---

## Шаг 2. Структура каталогов на VPS

```bash
ssh deploy@193.233.217.223
cd ~
mkdir -p processing-gateway/{app,data/postgres,init}
cd processing-gateway
```

Итоговая структура:
```
~/processing-gateway/
├── app/                  # исходники (rsync с локальной машины)
├── data/
│   └── postgres/         # volume Postgres
├── init/
│   └── 01-analytics-reader.sql   # init-скрипт
├── docker-compose.yml    # шаг 4
└── .env                  # шаг 3
```

> **Volumes MinIO/RabbitMQ/Seq не создаём** — они уже в `~/parser-service/data/`
> и пере-используются.

---

## Шаг 3. `.env` с секретами

`~/processing-gateway/.env`:

```ini
# Своя БД PG
PROCESSING_DB_PASSWORD=<сгенерированный шаг 1.1>
ANALYTICS_READER_PASSWORD=<сгенерированный шаг 1.1>

# X-Api-Key для QA-ручек (как у Parser, но свой)
GATEWAY_API_KEY=<сгенерированный шаг 1.1>

# === Шарятся с Parser-стэком — копируй точно те же значения, что в ~/parser-service/.env ===
MINIO_ACCESS_KEY=<копия из parser-service/.env>
MINIO_SECRET_KEY=<копия из parser-service/.env>
RABBIT_USER=<копия из parser-service/.env>
RABBIT_PASS=<копия из parser-service/.env>

# Seq ingestion-ключи (создадим в §7) — оставь пустыми пока, заполни на шаге 7
SEQ_INGESTION_KEY_GATEWAY=
SEQ_INGESTION_KEY_LLMSTUB=
```

Защитить права:
```bash
chmod 600 ~/processing-gateway/.env
```

---

## Шаг 4. `docker-compose.yml`

`~/processing-gateway/docker-compose.yml`:

```yaml
# Деплой PG в существующую сеть parser-service_internal.
# MinIO / RabbitMQ / Seq / nginx — берём из Parser-стэка (declared external).

services:
  processing-gateway:
    build:
      context: ./app
      dockerfile: src/ProcessingGateway/Dockerfile
    image: processing-gateway:latest
    restart: unless-stopped
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: "http://+:8080"

      ConnectionStrings__ProcessingDb: "Host=processing-db;Port=5432;Database=processing;Username=processing_user;Password=${PROCESSING_DB_PASSWORD}"

      # Parser в той же сети — резолвится по docker DNS.
      Parser__BaseUrl: "http://parser:8080"
      Parser__PollIntervalSeconds: "4"
      Parser__TaskTimeoutMinutes: "30"

      # MinIO Parser-стэка — внутренний http (TLS терминирует nginx внешним).
      S3__Endpoint: "http://minio:9000"
      S3__AccessKey: "${MINIO_ACCESS_KEY}"
      S3__SecretKey: "${MINIO_SECRET_KEY}"
      S3__BucketName: "obratka-jobs"

      # RabbitMQ Parser-стэка.
      RabbitMq__Host: "rabbitmq"
      RabbitMq__User: "${RABBIT_USER}"
      RabbitMq__Pass: "${RABBIT_PASS}"

      Llm__RequestQueue: "llm.requests"
      Llm__ResultQueue: "llm.results"
      Llm__ResultTimeoutMinutes: "30"

      # Web API ещё нет → AutoFinalize включён, см. IMPLEMENTATION_PLAN.md решение №6.
      Pipeline__AutoFinalizeWithoutAggregates: "true"
      Pipeline__AutoFinalizeAfterMinutes: "5"

      Gateway__ApiKey: "${GATEWAY_API_KEY}"

      Seq__ServerUrl: "http://seq:80"
      Seq__ApiKey: "${SEQ_INGESTION_KEY_GATEWAY}"
    depends_on:
      processing-db:
        condition: service_healthy
    networks: [parser-internal]
    # порты НЕ пробрасываем — снаружи только через nginx vhost gateway-dev.

  llm-stub:
    build:
      context: ./app
      dockerfile: tools/LlmStub/Dockerfile
    image: processing-gateway-llm-stub:latest
    restart: unless-stopped
    environment:
      DOTNET_ENVIRONMENT: Production
      RabbitMq__Host: "rabbitmq"
      RabbitMq__User: "${RABBIT_USER}"
      RabbitMq__Pass: "${RABBIT_PASS}"
      S3__Endpoint: "http://minio:9000"
      S3__AccessKey: "${MINIO_ACCESS_KEY}"
      S3__SecretKey: "${MINIO_SECRET_KEY}"
      S3__BucketName: "obratka-jobs"
      Seq__ServerUrl: "http://seq:80"
      Seq__ApiKey: "${SEQ_INGESTION_KEY_LLMSTUB}"
    networks: [parser-internal]
    # При появлении реального LLM-сервиса просто `docker compose stop llm-stub` —
    # PG продолжит публиковать в `llm.requests`, реальный LLM подцепится.

  processing-db:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: processing
      POSTGRES_USER: processing_user
      POSTGRES_PASSWORD: "${PROCESSING_DB_PASSWORD}"
      ANALYTICS_READER_PASSWORD: "${ANALYTICS_READER_PASSWORD}"
    volumes:
      - ./data/postgres:/var/lib/postgresql/data
      - ./init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U processing_user -d processing"]
      interval: 10s
      timeout: 5s
      retries: 5
    networks: [parser-internal]
    # порт 5432 НЕ пробрасываем (для DBeaver — SSH-туннель, см. §10).

networks:
  parser-internal:
    name: parser-service_internal
    external: true
```

Ключевые моменты:
- `networks.parser-internal.external: true` — мы **не создаём** сеть, а подключаемся к уже существующей `parser-service_internal`. Имя сети = `<имя-папки>_<имя-сети>` по правилу docker-compose, поэтому в Parser-каталоге `parser-service` сеть `internal` → полное имя `parser-service_internal`.
- Никаких `ports:` — снаружи доступен только через nginx (§8).
- `depends_on: processing-db` — единственная зависимость в нашем compose (на ресурсы Parser-стэка `depends_on` не работает между разными compose-файлами; healthcheck-ом мы их и не проверяем — service-discovery через DNS, MassTransit retry-ит при недоступности).

---

## Шаг 5. Init-скрипт `analytics_reader`

`~/processing-gateway/init/01-analytics-reader.sql`:

```sql
-- Создаёт read-only роль для Web API Analytics-модуля (ADR-011).
-- Выполняется ОДИН раз при первой инициализации Postgres-volume.
-- Прокидывание пароля через ENV: $ANALYTICS_READER_PASSWORD передаётся в контейнер
-- через docker-compose, postgres-image экспонирует его при выполнении init-скриптов.

\set analytics_password `echo "$ANALYTICS_READER_PASSWORD"`

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_reader') THEN
        EXECUTE format('CREATE ROLE analytics_reader LOGIN PASSWORD %L',
                       :'analytics_password');
    END IF;
END $$;

GRANT CONNECT ON DATABASE processing TO analytics_reader;
GRANT USAGE ON SCHEMA public TO analytics_reader;
-- GRANT-ы на конкретные таблицы выдаст EF-миграция Initial.cs после CREATE TABLE.
```

Проверь права:
```bash
chmod 644 ~/processing-gateway/init/01-analytics-reader.sql
```

> Замечание: если Postgres-image не подхватывает `ANALYTICS_READER_PASSWORD` из env
> в `psql`-скриптах (зависит от версии), используй жёстко прописанный пароль и
> отдельно `chmod 600` на init-каталог. Альтернатива — выполнить CREATE ROLE
> руками после первого старта (см. §9.5 Troubleshooting).

---

## Шаг 6. Доставка кода

Цель — положить исходники в `~/processing-gateway/app/` так, чтобы локальный `docker compose build`
на VPS собрал образы. Два варианта.

### Вариант A — rsync с локальной машины (рекомендуется для итеративной разработки)

#### 6.A.1. Префлайт: где запускать rsync на Windows

`rsync` под нативным `cmd.exe` / PowerShell **не работает корректно** (проблемы с правами,
переводами строк, символьными ссылками). Запускай из:

- **Git Bash** (приходит с Git for Windows) — самый простой путь
- **WSL / WSL2** — путь к Windows-папкам через `/mnt/c/...`
- **MSYS2** — если уже установлен

В Git Bash проверь, что `rsync` установлен:
```bash
rsync --version
# Если "command not found" — установи через `pacman -S rsync` (MSYS2) или
# скачай rsync.exe для Git Bash отдельно (например, через MSYS2 или cwRsync).
```

#### 6.A.2. Префлайт: SSH-доступ

Удостоверься, что подключение по ключу работает (без пароля):
```bash
ssh deploy@193.233.217.223 "echo ok"
# Должно вывести "ok" без запроса пароля. Если запрашивает пароль —
# скопируй ключ: ssh-copy-id deploy@193.233.217.223
```

#### 6.A.3. Подготовить папки на VPS (один раз)

```bash
ssh deploy@193.233.217.223 '
    mkdir -p ~/processing-gateway/{app,data/postgres,init} &&
    chmod 700 ~/processing-gateway/data/postgres &&
    ls -la ~/processing-gateway/
'
# Должны появиться: app/, data/, init/
```

#### 6.A.4. Запустить rsync

В Git Bash на **локальной** машине:

```bash
cd /c/Users/nordWorkStudy/Desktop/Obratka/Processing-Gateway

rsync -avz --delete \
    --exclude='.git' \
    --exclude='bin' --exclude='obj' \
    --exclude='data' \
    --exclude='tests' \
    --exclude='docker-compose.yml' \
    --exclude='docker-compose.full.yml' \
    --exclude='docker-compose.with-parser.yml' \
    --exclude='parser-config' \
    --exclude='test-cases.md' \
    --exclude='/*.md' \
    ./ deploy@193.233.217.223:~/processing-gateway/app/
```

Флаги:
- `-a` — archive (preserve permissions, timestamps, symlinks)
- `-v` — verbose (показывает файлы)
- `-z` — compress on the wire (быстрее по медленному соединению)
- `--delete` — удалять на VPS файлы, которых нет локально (синхронизация в одну сторону).
  **Важно:** удаляет только в `app/`, не в `data/`.

Что исключаем и почему:
- `.git/` — на VPS git-история не нужна
- `bin/`, `obj/` — артефакты локальной сборки .NET (Linux-образ соберёт свои)
- `data/` — локальный volume Postgres/MinIO (на VPS свои volumes)
- `tests/` — на проде тесты не нужны (соберём только runtime)
- `docker-compose*.yml` — на VPS свой compose в `~/processing-gateway/docker-compose.yml`,
  локальные compose-файлы (для dev-стека с parser) не должны его перетереть
- `parser-config/` — для локальной разработки (override appsettings парсера в смешанном dev-compose)
- `test-cases.md` — postman-плейбук, на проде не нужен
- `/*.md` — `CLAUDE.md`, `IMPLEMENTATION_PLAN.md`, `LLM_INTEGRATION_FAQ.md`, `deploy-vps.md`
  в корне репо. Ведущий `/` означает «только в корне» — `.md` файлы внутри `src/` (если будут)
  поедут на VPS.

#### 6.A.5. Проверить результат

```bash
ssh deploy@193.233.217.223 'ls ~/processing-gateway/app/'
# Должно быть:
# ProcessingGateway.sln  src/  tools/  Directory.Build.props (если есть)
```

Заглянуть глубже:
```bash
ssh deploy@193.233.217.223 'tree -L 2 ~/processing-gateway/app/ 2>/dev/null || find ~/processing-gateway/app -maxdepth 2'
```
Внутри должны быть `src/ProcessingGateway/Dockerfile` и `tools/LlmStub/Dockerfile` —
их сборку запустит `docker compose build`.

#### 6.A.6. Re-deploy (повторное обновление кода)

После любых изменений в коде — на локальной машине:
```bash
cd /c/Users/nordWorkStudy/Desktop/Obratka/Processing-Gateway

# 1. Залить изменения
rsync -avz --delete <те же флаги что выше> \
    ./ deploy@193.233.217.223:~/processing-gateway/app/

# 2. Пересобрать и перезапустить (на VPS, через ssh)
ssh deploy@193.233.217.223 '
    cd ~/processing-gateway &&
    docker compose build processing-gateway llm-stub &&
    docker compose up -d processing-gateway llm-stub
'
```

`processing-db` re-deploy'ить **не нужно** (Postgres-образ из docker hub, не из Dockerfile).
EF-миграции применяются **в момент старта** контейнера PG — `docker compose up -d processing-gateway`
автоматически выполнит новые миграции, если они есть.

#### 6.A.7. Типичные ошибки

- **`rsync: command not found`** — установи rsync (см. 6.A.1).
- **`Permission denied (publickey)`** — нет SSH-ключа на VPS (см. 6.A.2: `ssh-copy-id`).
- **`Permission denied`** при записи в `~/processing-gateway/app/` — возможно, директория
  принадлежит `root` (если кто-то делал `mkdir` через `sudo`). Фикс на VPS:
  `sudo chown -R deploy:deploy ~/processing-gateway/`.
- **Очень медленно идёт первая синхронизация** — нормально, передаются ~50–100 МБ исходников.
  Последующие — секунды.
- **`failed to set times: Operation not permitted`** — игнорируется, не критично
  (rsync не может выставить timestamp на FAT/SMB-смонтированных каталогах).

### Вариант B — git deploy key (для CI/CD / pull-based deploy)

Один раз на VPS:
```bash
ssh-keygen -t ed25519 -C "processing-gateway-vps-deploy" -f ~/.ssh/github_processing -N ""
cat ~/.ssh/github_processing.pub
# добавить ключ в GitHub repo → Settings → Deploy keys (read-only)

# дополнить ~/.ssh/config
cat >> ~/.ssh/config <<EOF
Host github.com-processing
    HostName github.com
    IdentityFile ~/.ssh/github_processing
    IdentitiesOnly yes
EOF

git clone git@github.com-processing:<user>/<processing-gateway-repo>.git ~/processing-gateway/app
```

Дальше обновления:
```bash
cd ~/processing-gateway/app && git pull
cd ~/processing-gateway && docker compose build processing-gateway llm-stub && docker compose up -d
```

> **Когда использовать B вместо A:** если делаешь GitHub Actions / GitLab CI с автодеплоем,
> или работаешь несколько разработчиков (тогда rsync с разных машин будет конфликтовать).
> На MVP с одним разработчиком и итерациями — Вариант A быстрее и проще.

---

## Шаг 7. Seq ingestion ключи

### 7.1. Открыть Seq UI

```
https://logs.193.233.217.223.sslip.io
```
Логин/пароль — те, что заводил для Parser-стэка (см. `Parser-Service/deploy-vps.md` §16.5).

### 7.2. Создать два API-ключа

**Settings → API Keys → New API Key:**

1. Title: `processing-gateway`
   - Permissions: только **Ingest**
   - Applied properties: `Service: ProcessingGateway`, `Environment: Production` (опционально)
   - Сохранить → **скопировать ключ немедленно** (показывается один раз)

2. Title: `llm-stub`
   - Permissions: только **Ingest**
   - Applied properties: `Service: LlmStub`, `Environment: Production`
   - Сохранить → скопировать

### 7.3. Заполнить `.env`

```bash
nano ~/processing-gateway/.env
# заполнить:
SEQ_INGESTION_KEY_GATEWAY=<ключ для processing-gateway>
SEQ_INGESTION_KEY_LLMSTUB=<ключ для llm-stub>
```

---

## Шаг 8. nginx vhost для PG Status API

Status API публичен внутри docker-сети, но на стенде нужен **временный** внешний доступ
(до прихода Web API), чтобы фронтенд / отладка ходили снаружи. Решение — отдельный
nginx-vhost с allowlist + X-Api-Key middleware на стороне PG (атрибут
`RequireQaApiKey` уже на всех QA-ручках; основной `/api/analyses/{jobId}/status` —
без ключа, защищается только nginx allowlist).

### 8.1. Расширить TLS-сертификат

Текущий SAN-сертификат `parser.193.233.217.223.sslip.io` покрывает `parser` и `logs`
(одним cert'ом, см. `~/parser-service/nginx/conf.d/logs.conf` — он использует тот же
`/etc/letsencrypt/live/parser.193.233.217.223.sslip.io/`). Добавляем к нему `gateway-dev`
через `--expand` (опционально сразу `s3-admin` для §9):

**Внимание: команда выполняется из `~/parser-service/`** (не из `~/processing-gateway/`).
Сервис `certbot` определён в parser-compose. Если запустишь из PG-папки — получишь
`no such service: certbot`.

```bash
ssh deploy@193.233.217.223
cd ~/parser-service     # ← обязательно перейти сюда

# Запускаем certbot-контейнер с нужными флагами (entrypoint обычным запуском —
# демон-цикл renew, поэтому override через --entrypoint).
docker compose run --rm --entrypoint certbot certbot \
    certonly \
    --webroot -w /var/www/certbot \
    -d parser.193.233.217.223.sslip.io \
    -d logs.193.233.217.223.sslip.io \
    -d gateway-dev.193.233.217.223.sslip.io \
    -d s3-admin.193.233.217.223.sslip.io \
    --expand \
    --email qwertyleo2121@gmail.com \
    --agree-tos \
    --no-eff-email
```

> Замечание: nginx должен быть **запущен** на 80 порту во время выпуска (ACME challenge
> идёт через `/.well-known/acme-challenge/` — webroot `/var/www/certbot`). Это уже так
> по умолчанию — `nginx` поднят, а `parser.conf` отдаёт challenge на 80 порту.

Проверь SAN:
```bash
docker compose run --rm --entrypoint certbot certbot certificates
# или вручную:
openssl x509 -in ~/parser-service/certbot/conf/live/parser.193.233.217.223.sslip.io/fullchain.pem \
    -noout -text | grep -A1 "Subject Alternative Name"
# Должны быть все 4 домена.
```

### 8.2. Allowlist (твой текущий список IP)

Твой текущий allowlist в `parser.conf`:
```
193.124.183.35/32
95.105.64.203/32
93.183.90.230/32
150.251.146.249/32
```
Используем тот же набор для `gateway-dev` (если нужны другие IP — добавь в момент создания файла).

### 8.3. nginx-vhost

`~/parser-service/nginx/conf.d/gateway.conf` (живёт в Parser-каталоге, потому что nginx — там):

```nginx
# IP allowlist: кто может ходить на Processing Gateway (тот же, что для parser)
geo $gateway_allowed {
    default              0;
    193.124.183.35/32    1;
    95.105.64.203/32     1;
    93.183.90.230/32     1;
    150.251.146.249/32   1;
}

# 80 → 443 (с обработкой ACME)
server {
    listen 80;
    server_name gateway-dev.193.233.217.223.sslip.io;

    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / { return 301 https://$host$request_uri; }
}

server {
    listen 443 ssl;
    http2 on;
    server_name gateway-dev.193.233.217.223.sslip.io;

    ssl_certificate     /etc/letsencrypt/live/parser.193.233.217.223.sslip.io/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/parser.193.233.217.223.sslip.io/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_prefer_server_ciphers on;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;

    add_header Strict-Transport-Security "max-age=31536000" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;

    if ($gateway_allowed = 0) { return 403; }

    client_max_body_size 8m;

    location /.well-known/acme-challenge/ { root /var/www/certbot; }

    location / {
        proxy_pass http://processing-gateway:8080;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Connection        "";

        # QA-эндпоинты ингеста output.json могут быть тяжёлыми — даём запас.
        proxy_read_timeout    300s;
        proxy_send_timeout    300s;
        proxy_connect_timeout 30s;
    }
}
```

### 8.4. Применить

```bash
cd ~/parser-service
docker compose exec nginx nginx -t
docker compose exec nginx nginx -s reload
```

Если `nginx -t` ругается «host not found in upstream `processing-gateway`» — это
ожидаемо до того как мы поднимем PG-стэк (имя контейнера ещё не зарезолвится).
**Что делать:** перейди к §9, подними PG-стэк, потом вернись и сделай `nginx -s reload`.

> nginx статически резолвит upstream при старте/reload — после поднятия PG-стэка
> нужно `nginx -s reload` ещё раз. Альтернатива — `resolver 127.0.0.11 valid=10s;`
> + переменная в `proxy_pass` для динамического резолва. Не критично сейчас.

---

## Шаг 8.5 (опционально). MinIO Web Console наружу

Полезно для отладки: видеть содержимое бакета `obratka-jobs` через UI вместо `mc ls` в shell.
MinIO console уже слушает `:9001` внутри контейнера (см. `--console-address ":9001"` в parser
docker-compose), но порт наружу не пробрасывается. Проксируем через nginx — без изменения
parser-compose, только новый vhost.

**Безопасность:**
- IP allowlist nginx — те же 4 IP, что для parser/gateway.
- Логин/пароль MinIO root user (`MINIO_ROOT_USER` = `MINIO_ACCESS_KEY` из parser `.env`).
- Console использует WebSocket (live-обновления) — нужны `Upgrade`/`Connection` заголовки
  как в `logs.conf`.

`~/parser-service/nginx/conf.d/s3-admin.conf`:

```nginx
geo $s3admin_allowed {
    default              0;
    193.124.183.35/32    1;
    95.105.64.203/32     1;
    93.183.90.230/32     1;
    150.251.146.249/32   1;
}

server {
    listen 80;
    server_name s3-admin.193.233.217.223.sslip.io;
    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / { return 301 https://$host$request_uri; }
}

server {
    listen 443 ssl;
    http2 on;
    server_name s3-admin.193.233.217.223.sslip.io;

    ssl_certificate     /etc/letsencrypt/live/parser.193.233.217.223.sslip.io/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/parser.193.233.217.223.sslip.io/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    add_header Strict-Transport-Security "max-age=31536000" always;

    if ($s3admin_allowed = 0) { return 403; }

    # MinIO console грузит бакеты по 5 МБ кусочками + поддерживает upload
    client_max_body_size 0;
    proxy_buffering off;
    proxy_request_buffering off;
    chunked_transfer_encoding on;

    proxy_read_timeout 600s;
    proxy_send_timeout 600s;

    location / {
        proxy_pass http://minio:9001;
        proxy_http_version 1.1;
        proxy_set_header Host              $http_host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade           $http_upgrade;
        proxy_set_header Connection        "upgrade";
    }
}
```

Применить:
```bash
cd ~/parser-service
docker compose exec nginx nginx -t
docker compose exec nginx nginx -s reload
```

Открой `https://s3-admin.193.233.217.223.sslip.io` → логин = `MINIO_ACCESS_KEY`,
пароль = `MINIO_SECRET_KEY` (значения из `~/parser-service/.env`).

> **Вариант: S3 API наружу (не console)**. Если нужно ходить в MinIO из локального
> aws-cli / DBeaver / SDK — это **другой** vhost (`s3.193.233.217.223.sslip.io` →
> `proxy_pass http://minio:9000`). Не путать с console (порт 9001). На MVP не делаем —
> внутрисетевой доступ из PG/Parser достаточен.

---

## Шаг 9. Первый запуск

```bash
cd ~/processing-gateway
docker compose up -d --build
```

Что должно произойти:
1. Сборка двух образов (~5–10 мин при первом запуске).
2. Старт `processing-db` → init-скрипт создаёт роль `analytics_reader` → healthy.
3. Старт `processing-gateway` → миграция EF (`Initial`) применяется → создаются таблицы + GIN-индекс + GRANT-ы аналитику. Затем сервис слушает HTTP на `:8080` и подписывается на RabbitMQ.
4. Старт `llm-stub` → подписывается на `llm.requests`.

Логи на каждом этапе:
```bash
docker compose logs -f processing-gateway        # Ctrl+C когда увидел "ProcessingGateway started"
docker compose logs --tail=20 llm-stub           # должно быть "LlmStub started"
docker compose logs --tail=20 processing-db      # ищем "database system is ready to accept connections"
```

После — обнови nginx (он на этом моменте впервые увидит upstream `processing-gateway`):
```bash
docker compose -f ~/parser-service/docker-compose.yml exec nginx nginx -s reload
```

---

## Шаг 10. Проверка

### 10.1. С whitelisted IP (твоя машина)

```bash
# Liveness
curl -i https://gateway-dev.193.233.217.223.sslip.io/health/live
# Ожидание: 200 + {"status":"alive"}

# Полная диагностика
curl -i -H "X-Api-Key: <GATEWAY_API_KEY>" \
    https://gateway-dev.193.233.217.223.sslip.io/api/qa/health/dependencies
# Ожидание: 200, ok=true для postgres / s3 / parser

# Запуск анализа (внимание — Parser реально соберёт отзывы, нужен прокси-конфиг)
curl -X POST https://gateway-dev.193.233.217.223.sslip.io/api/qa/analyses \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: <GATEWAY_API_KEY>" \
    -d '{
      "companyId": "11111111-1111-1111-1111-111111111111",
      "branches": [{
        "branchId": "22222222-2222-2222-2222-222222222222",
        "source": "yandex",
        "externalId": "1124715036",
        "externalUrl": "https://yandex.ru/maps/org/artel/1124715036/"
      }]
    }'
```

### 10.2. С non-whitelisted IP

```bash
curl -i https://gateway-dev.193.233.217.223.sslip.io/health/live
# Ожидание: 403 от nginx (до PG не доходит)
```

### 10.3. Postgres через DBeaver (SSH-туннель)

Порт БД наружу не пробрасываем. Чтобы посмотреть данные:

```bash
ssh -L 5433:localhost:5433 deploy@193.233.217.223
```

> На VPS добавь временный port-mapping в `~/processing-gateway/docker-compose.yml`:
> ```yaml
> processing-db:
>   ports:
>     - "127.0.0.1:5433:5432"   # только loopback VPS — снаружи cloud-firewall режет
> ```
> Применить: `docker compose up -d processing-db`.

Дальше в DBeaver: `localhost:5433`, db `processing`, user `processing_user`, password из `.env`.

После работы — убери port-mapping, `docker compose up -d processing-db` снова.

### 10.4. Что проверить в Seq

`https://logs.193.233.217.223.sslip.io` → фильтр:
```
Service = 'ProcessingGateway' OR Service = 'LlmStub'
```
Должны видеть события `ProcessingGateway started`, `EF migrations applied`, `LlmStub started`,
консьюмеры подключились к RabbitMQ.

---

## Шаг 11. Будущая ротация: отдельный пользователь RabbitMQ

Сейчас PG ходит под пользователем `${RABBIT_USER}` Parser-а. Для production-прав
правильно выделить отдельного пользователя `gateway` с правами только на нужные queues.

```bash
docker compose -f ~/parser-service/docker-compose.yml exec rabbitmq \
    rabbitmqctl add_user gateway "<новый-пароль>"
docker compose -f ~/parser-service/docker-compose.yml exec rabbitmq \
    rabbitmqctl set_permissions -p / gateway \
        "^(StartAnalysisCommand|LlmResultMessage|AggregatesReadyEvent|llm\.requests|.*outbox.*)$" \
        "^(StartAnalysisCommand|LlmResultMessage|AggregatesReadyEvent|llm\.requests|llm\.results|.*Event.*|.*Command.*)$" \
        ".*"
```

(Регулярки уточнить под фактические имена очередей MassTransit — поставить после
первого запуска, посмотрев `rabbitmqctl list_queues`.)

Потом обновить `.env` и `docker compose up -d processing-gateway llm-stub`.

Этот шаг откладывается до прода или появления нескольких сервисов в брокере.

---

## Шаг 12. Бэкапы Postgres

`crontab -e`:
```
0 3 * * *   docker compose -f /home/deploy/processing-gateway/docker-compose.yml \
              exec -T processing-db pg_dump -U processing_user processing | \
              gzip > /home/deploy/backups/processing-$(date +\%F).sql.gz
0 4 * * *   find /home/deploy/backups/processing-*.sql.gz -mtime +14 -delete
```

Создаст ежедневный дамп БД, удалит старше 14 дней.

---

## Шаг 13. Чек-лист «готов к работе»

- [ ] `docker compose ps` — `processing-gateway`, `processing-db`, `llm-stub` все `Up` (healthy для postgres)
- [ ] `curl https://gateway-dev.../health/live` с whitelisted IP → 200
- [ ] `curl https://gateway-dev.../health/live` с non-whitelisted IP → 403
- [ ] `curl -H "X-Api-Key: ..." .../api/qa/health/dependencies` → все три зелёные
- [ ] В Seq `Service = 'ProcessingGateway'` показывает `Started`
- [ ] EF-миграции применились (см. snapshot в init-логах: `EF migrations applied`)
- [ ] `analytics_reader` создан (`docker compose exec processing-db psql -U processing_user -d processing -c '\du'` → видно роль)
- [ ] Бэкапы в cron
- [ ] Сертификат `parser.193.233.217.223.sslip.io` SAN включает: `parser`, `logs`, `gateway-dev` (+ `s3-admin` если делал §8.5). Проверка: `docker compose -f ~/parser-service/docker-compose.yml run --rm --entrypoint certbot certbot certificates`

---

## Шаг 14. Troubleshooting

### `error: external network parser-service_internal not found`

Сеть не существует. Проверь:
```bash
docker network ls | grep parser-service
```
Если пусто — Parser-стэк не работает. Запусти `cd ~/parser-service && docker compose up -d`.

### `processing-db` не стартует, init-скрипт ругается на `:'analytics_password'`

Postgres-version не подхватил env-переменную в psql-скрипте. Workaround — вручную:

```bash
docker compose exec processing-db psql -U processing_user -d processing -c \
    "CREATE ROLE analytics_reader LOGIN PASSWORD '<пароль из .env>';"
```

И захардкодить пароль в init-скрипт перед следующим volume-recreate (или удалить
скрипт совсем — роль уже создана).

### PG не видит Parser

```bash
docker compose -f ~/processing-gateway/docker-compose.yml exec processing-gateway \
    sh -c "wget -qO- http://parser:8080/health/live"
```
Если `Connection refused` — Parser не в той же сети. Проверь `external: true` в compose
и имя сети `parser-service_internal` (с подчёркиванием).

### `nginx -t`: `host not found in upstream "processing-gateway"`

PG-контейнер ещё не поднят. Сначала `docker compose -f ~/processing-gateway/... up -d`,
потом `nginx -s reload`.

### `403 Forbidden` от nginx с правильного IP

Проверь IP в `geo $gateway_allowed` блоке `~/parser-service/nginx/conf.d/gateway.conf`.
Свой текущий IP: `curl ifconfig.me`. Не забудь `nginx -s reload` после правки.

### Certbot не выпустил сертификат (`Failed authorization procedure`)

ACME challenge требует, чтобы 80 порт был открыт И `~/parser-service/certbot/www/`
монтировался в nginx как `/var/www/certbot`. Проверь:
```bash
ls ~/parser-service/certbot/www/   # должна быть папка (даже пустая)
docker compose -f ~/parser-service/docker-compose.yml exec nginx \
    ls /var/www/certbot              # тоже должна быть видна
docker compose -f ~/parser-service/docker-compose.yml exec nginx \
    cat /etc/nginx/conf.d/gateway.conf | grep acme-challenge
# Должна быть `location /.well-known/acme-challenge/ { root /var/www/certbot; }` в
# 80-вом server-блоке.
```
Затем `--dry-run` для отладки (без квоты Let's Encrypt):
```bash
docker compose -f ~/parser-service/docker-compose.yml run --rm --entrypoint certbot certbot \
    certonly --dry-run --webroot -w /var/www/certbot \
    -d gateway-dev.193.233.217.223.sslip.io
```

### MassTransit license error в логах PG

Должен быть устранён переходом на `MassTransit 8.*` в csproj. Если всё-таки появился:
```bash
docker compose -f ~/processing-gateway/... exec processing-gateway \
    grep -r "MassTransit" /app/*.deps.json | grep -i version
```
Должно быть `8.x.x`. Если 9.x — пересобери образ (`docker compose build --no-cache processing-gateway`).

### `Pending model changes` от EF

Возможно volume Postgres от предыдущего деплоя со старыми миграциями. Если данных не жалко:
```bash
docker compose down
sudo rm -rf data/postgres/*
docker compose up -d
```

### Status API долго отвечает

В Seq фильтр по `RequestPath = '/api/analyses/...'` — если elapsed > 1s, проверь
Postgres-нагрузку. На MVP это не должно быть проблемой.

---

## Шаг 15. Подключение реального LLM-сервиса (`llm-pipeline`)

Заменяем `LlmStub` (заглушка на C#) на настоящий Python-сервис из репо `llm_pipline/`.
Контракт — `LLM_PYTHON_QUICKSTART.md` + `LLM_INTEGRATION_FAQ.md`.

> **⚠️ Schema mismatch на момент деплоя.** PG-код пока на `schema_version=1.0`, LLM-сервис
> публикует `2.0` (два output-файла, raw JSON, новый формат aspects). End-to-end ингест
> результатов **не работает** до миграции PG-кода на 2.0 (см. backlog: код / тесты /
> EF-миграция). Что **работает** на этом этапе:
> - LLM-pipeline стартует, подписывается на `llm.requests`
> - REST `/health/live` и `/status/{jobId}` отвечают
> - QA-ручка `/qa/analyze` для прогона `input.json` в обход RabbitMQ
>
> Что **не работает**: запуск анализа через `POST /api/qa/analyses` PG → ингест ответа
> провалится, job застрянет в `sent_to_llm` и через timeout уйдёт в `failed`. Это **ожидаемо**.

### 15.1. Структура каталогов на VPS

LLM-сервис разворачиваем в **отдельной папке-сиблинге** (как parser/processing-gateway):

```
/home/deploy/
├── parser-service/         (parser-стек: nginx, certbot, parser, MinIO, RabbitMQ, Seq)
├── processing-gateway/     (PG-стек: pg, processing-db, llm-stub)
└── llm-pipeline/           ← НОВАЯ папка для исходников LLM
```

Создать на VPS:
```bash
ssh deploy@193.233.217.223
mkdir -p ~/llm-pipeline/{app,data,logs}
chmod 700 ~/llm-pipeline/data ~/llm-pipeline/logs
```

`data/` и `logs/` будут смонтированы как volumes — туда пишется SQLite job_state и ротация логов.

### 15.2. Доставка кода (rsync с локальной машины)

В Git Bash на локальной машине:

```bash
cd /c/Users/nordWorkStudy/Desktop/Obratka/llm_pipline

rsync -avz --delete \
    --exclude='.git' \
    --exclude='__pycache__' --exclude='.pytest_cache' \
    --exclude='qa_outputs' --exclude='out' \
    --exclude='data' --exclude='logs' \
    --exclude='*.log' \
    --exclude='.env*' \
    --exclude='input*.json' --exclude='llm_response.json' \
    --exclude='build.log' \
    ./ deploy@193.233.217.223:~/llm-pipeline/app/
```

Что исключаем:
- `.env*` — секреты (`OPENROUTER_API_KEY`) пробросим отдельно через `~/llm-pipeline/.env`
- `qa_outputs/`, `out/`, `data/`, `logs/` — local artifacts dev-прогонов
- `input*.json`, `llm_response.json` — fixture-файлы локальной отладки

После rsync на VPS будет:
```
~/llm-pipeline/app/
├── Dockerfile.worker
├── pyproject.toml
├── poetry.lock
├── src/
├── tasks/
├── docs/
└── README.md
```

### 15.3. Секреты `.env`

Создать `~/llm-pipeline/.env`:

```bash
cat > ~/llm-pipeline/.env <<'EOF'
# OpenRouter API key (взять у LLM-команды)
OPENROUTER_API_KEY=sk-or-v1-xxxxxxxxxxxxxxxxxxxxxxxx

# Подключение к infra parser-стека (имена docker-сервисов)
RABBIT_URL=amqp://${RABBIT_USER}:${RABBIT_PASS}@rabbitmq:5672/
S3_ENDPOINT=http://minio:9000
S3_ACCESS_KEY=${MINIO_ACCESS_KEY}
S3_SECRET_KEY=${MINIO_SECRET_KEY}
S3_BUCKET=obratka-jobs

# REST + state
LLM_HTTP_PORT=8000
LLM_STATE_DB=/app/data/job_state.sqlite
LOGS_DIR=/app/logs
LOG_LEVEL=INFO
EOF

chmod 600 ~/llm-pipeline/.env
```

`${RABBIT_USER}` / `${RABBIT_PASS}` / `${MINIO_*}` — **те же значения**, что в `~/parser-service/.env`.
Скопируй их вручную (compose не подтянет переменные из чужого `.env`-файла, см. §3 этого документа
по принципу).

### 15.4. Добавить сервис в `~/processing-gateway/docker-compose.yml`

Открой compose:
```bash
nano ~/processing-gateway/docker-compose.yml
```

Добавь блок `llm-pipeline` рядом с `llm-stub`:

```yaml
  llm-pipeline:
    build:
      context: ../llm-pipeline/app
      dockerfile: Dockerfile.worker
    image: obratka-llm-worker:latest
    restart: unless-stopped
    env_file:
      - ../llm-pipeline/.env
    volumes:
      - ../llm-pipeline/data:/app/data
      - ../llm-pipeline/logs:/app/logs
    networks: [parser-internal]
    healthcheck:
      test: ["CMD-SHELL", "python -c \"import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8000/health/live', timeout=2).getcode()==200 else 1)\""]
      interval: 15s
      timeout: 5s
      retries: 5
      start_period: 30s
    # порт 8000 НЕ пробрасываем — REST доступен только из docker-сети по http://llm-pipeline:8000
```

> **Почему `context: ../llm-pipeline/app`** — compose находится в `~/processing-gateway/`,
> а исходники LLM в `~/llm-pipeline/app/`. Build context относительный к compose-файлу.

### 15.5. Остановить `LlmStub`

Stub и pipeline **не могут работать одновременно** — оба слушают `llm.requests`, и
сообщения будут round-robin распределяться между ними. Останавливаем stub:

```bash
cd ~/processing-gateway
docker compose stop llm-stub
docker compose rm -f llm-stub      # удалить контейнер чтобы не висел в `docker ps -a`
```

> Можно оставить service в compose-файле — он просто не запустится при следующем `up -d`,
> если ты не упомянёшь его явно. Но в `docker compose ps` он будет показываться как `Exited`.
> Чище — закомментировать его блок целиком.

### 15.6. Сборка и запуск

```bash
cd ~/processing-gateway
docker compose build llm-pipeline
docker compose up -d llm-pipeline
```

Сборка ~5–8 минут при первом запуске (poetry install + langdetect компиляция). Последующие — секунды.

Проверь:
```bash
docker compose ps llm-pipeline
# obratka-llm-worker   Up (healthy)

docker compose logs --tail=30 llm-pipeline
# Ожидание:
# - "uvicorn running on http://0.0.0.0:8000"
# - "LLM service listening on llm.requests"
# - "Connected to RabbitMQ"
```

### 15.7. Smoke-тест из контейнера PG

REST-эндпоинт LLM доступен по docker DNS:

```bash
# Health
docker compose exec processing-gateway sh -c \
    "wget -qO- http://llm-pipeline:8000/health/live"
# {"status":"alive"}

# Status неизвестного job-а
docker compose exec processing-gateway sh -c \
    "wget -qO- http://llm-pipeline:8000/status/00000000-0000-0000-0000-000000000000 || true"
# 404 + {"detail":{"analysis_job_id":"...","status":"unknown"}}
```

### 15.8. RabbitMQ — убедиться что подписка активна

Через CLI:
```bash
docker compose -f ~/parser-service/docker-compose.yml exec rabbitmq \
    rabbitmqctl list_queues name messages consumers
```
Очередь `llm.requests` должна иметь `consumers >= 1`. Если 0 — pipeline не подцепился (смотри
логи llm-pipeline).

### 15.9. (Опционально) QA-ручка LLM для тестирования контракта в обход PG

Полезно когда хочешь убедиться что LLM-сервис работает на реальных отзывах **без зависимости
от schema mismatch с PG**:

```bash
# В compose добавь environment:
#   OBRATKA_QA_ENABLED: "true"
#   LLM_QA_OUTPUT_DIR: /app/qa_outputs
# и volume:
#   - ../llm-pipeline/qa_outputs:/app/qa_outputs
docker compose up -d llm-pipeline

# Положи sample input.json (например, с локальной машины через scp) в ~/llm-pipeline/qa_outputs/
# Прогон через REST:
docker compose exec processing-gateway sh -c '
    wget -qO- --post-file=/some/input.json \
        --header="Content-Type: application/json" \
        http://llm-pipeline:8000/qa/analyze
'
# В response — путь к output_*.json в qa_outputs/<job_id>/
```

В проде `OBRATKA_QA_ENABLED` **должен быть `false` или не указан** — это dev-only ручка.

### 15.10. Когда переходим на schema 2.0 (после миграции PG-кода)

Из backlog'а (см. CLAUDE.md PG → раздел «Изменения относительно 1.0»):

1. PG-код мигрирован на 2.0 (LlmContracts, миграция БД, `UseRawJsonDeserializer()` для `llm.results`)
2. Новый Initial-migration применён → таблицы `analysis_recommendations`, обновлённый `review_llm_results.aspects`
3. `LlmStub` обновлён под новый формат (или удалён)
4. На VPS:
   ```bash
   cd ~/processing-gateway
   docker compose down
   sudo rm -rf data/postgres/*           # дев-данные не жалко
   docker compose up -d --build
   ```
   PG переподнимется с новой схемой, `llm-pipeline` уже подключён → end-to-end заработает.

---

## Что **не** покрыто этим документом

- Реальный LLM-сервис подключается через **§15** этого документа. Полная e2e-интеграция
  (PG ингестит результаты) требует миграции PG-кода на schema_version 2.0 — это в backlog.
- Web API — после его деплоя удалить временный nginx-vhost `gateway-dev.conf`
  (Status API становится строго внутренним).
- Hangfire-планировщик мониторингов — внутри Web API, не PG.
- LlmStatusReconciler (REST fallback) — отложен до prod-инцидента, см.
  `IMPLEMENTATION_PLAN.md` Этап 7.
