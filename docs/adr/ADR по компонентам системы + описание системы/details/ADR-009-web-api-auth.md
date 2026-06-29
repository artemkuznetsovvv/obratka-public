# ADR-009: Web API — ответственность, аутентификация и авторизация

## Context

Web API — единственная точка входа фронтенда (BFF).
Он отвечает за аутентификацию пользователей, ролевую авторизацию,
управление бизнес-сущностями и оркестрацию запросов к downstream-сервисам.

**Роли в системе (только две):**
- **User** — клиент платформы: запускает анализы, смотрит дашборд, управляет мониторингами
- **Admin** — оператор: управляет пользователями, смотрит логи ошибок, имеет доступ к Hangfire Dashboard

**Требования к auth:**
- Регистрация / логин / выход
- Stateless: сервис должен масштабироваться горизонтально без sticky sessions
- SPA (React) общается через HTTP/JSON — токен передаётся с каждым запросом
- Refresh без повторного логина (сессия на несколько дней)
- Стек: .NET 8, PostgreSQL уже в системе

## Decision

### ASP.NET Core Identity + JWT Bearer + Refresh Token в httpOnly Cookie

Три компонента, у каждого своя роль:

| Компонент | Зачем |
|-----------|-------|
| **ASP.NET Core Identity** | Хранит пользователей, хеши паролей, роли в PostgreSQL. Управление через `UserManager<T>`, `RoleManager<T>` |
| **JWT Bearer** | Короткоживущий access token (15 мин). Фронтенд кладёт в заголовок `Authorization: Bearer <token>`. Stateless — сервер не хранит сессию |
| **Refresh Token** | Долгоживущий (7 дней), хранится в httpOnly cookie. Используется для обновления access token без повторного логина |

```
NuGet: Microsoft.AspNetCore.Identity.EntityFrameworkCore
       Microsoft.AspNetCore.Authentication.JwtBearer
       Npgsql.EntityFrameworkCore.PostgreSQL (уже есть)
```

#### Почему JWT + httpOnly refresh, а не только JWT в localStorage

| Подход | XSS | CSRF | Complexity |
|--------|-----|------|------------|
| JWT в localStorage | Уязвим (JS читает токен) | Нет | Низкая |
| JWT в httpOnly cookie | Защищён | Уязвим | Средняя |
| **Access JWT в памяти SPA + refresh в httpOnly cookie** | Защищён | Нет (SameSite=Strict) | Средняя |

Access token живёт 15 минут — даже при XSS-атаке окно минимально.
Refresh token в httpOnly cookie недоступен из JS.
`SameSite=Strict` устраняет CSRF для refresh endpoint.

#### Почему не Session / Cookie-only

Stateful sessions требуют shared session store (Redis) при горизонтальном масштабировании.
JWT stateless — любой инстанс Web API валидирует токен по секрету, без обращения к БД.

#### Почему не OpenIdConnect / отдельный Identity Server

Keycloak, IdentityServer — правильный выбор при федерации идентичностей, SSO,
OAuth2 для third-party clients. На MVP: два типа пользователей, один клиент (наш SPA),
нет third-party интеграций. ASP.NET Core Identity + JWT закрывает всё без
дополнительного сервиса.

### Схема таблиц (генерирует Identity через EF Core миграции)

ASP.NET Core Identity создаёт стандартные таблицы автоматически:

```sql
-- Генерируется Identity (не пишем вручную):
AspNetUsers          -- пользователи (email, passwordHash, ...)
AspNetRoles          -- роли ('User', 'Admin')
AspNetUserRoles      -- связь пользователь ↔ роль
AspNetUserTokens     -- refresh tokens (или custom таблица)

-- Расширение пользователя (наши поля):
-- Наследуем ApplicationUser : IdentityUser<Guid>
-- и добавляем поля через EF миграцию
```

```csharp
public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsBlocked { get; set; }
    public string? TelegramChatId { get; set; }   // для уведомлений
}
```

Refresh tokens — отдельная таблица для контроля инвалидации:

```sql
CREATE TABLE refresh_tokens (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID NOT NULL REFERENCES "AspNetUsers"(Id),
    token       VARCHAR(500) NOT NULL UNIQUE,
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_at  TIMESTAMPTZ,                      -- NULL = активен
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Авторизация: роли + политики

```csharp
// Program.cs
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireRole("Admin"));

    options.AddPolicy("AuthenticatedUser", policy =>
        policy.RequireAuthenticatedUser());
});
```

```csharp
// Применение:
[Authorize]                          // любой авторизованный
[Authorize(Policy = "AdminOnly")]    // только Admin

// Или через роль напрямую:
[Authorize(Roles = "Admin")]
```

Фронтенд получает роль из JWT claims и скрывает/показывает UI-элементы.
Сервер валидирует роль независимо — UI-ограничения не являются защитой.

### Конфигурация JWT

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
```

```json
// appsettings.json (секрет — из переменной окружения или secrets)
{
  "Jwt": {
    "Secret": "<min 32 chars, from env JWT__SECRET>",
    "Issuer": "obratka-api",
    "Audience": "obratka-spa",
    "AccessTokenExpiryMinutes": 15,
    "RefreshTokenExpiryDays": 7
  }
}
```

## Зоны ответственности Web API

### Auth

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/auth/register` | POST | Регистрация → создаёт User, выдаёт токены |
| `/auth/login` | POST | Логин → возвращает access JWT + refresh cookie |
| `/auth/refresh` | POST | Обменивает refresh cookie на новый access JWT |
| `/auth/logout` | POST | Отзывает refresh token, очищает cookie |
| `/auth/me` | GET | Текущий пользователь (id, email, role, имя) |

### Профиль пользователя

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/profile` | GET / PATCH | Просмотр и обновление данных профиля |
| `/profile/telegram` | POST | Привязка Telegram Chat ID |
| `/profile/notifications` | GET / PATCH | Настройки уведомлений |

### Компании и филиалы

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/companies` | GET | Список компаний пользователя |
| `/companies/search` | GET | Поиск карточек в источниках (→ Parser Service search) |
| `/companies` | POST | Создание компании с выбранными филиалами |
| `/companies/{id}` | GET / PATCH / DELETE | Управление компанией |
| `/companies/{id}/branches` | GET | Список сохранённых филиалов |
| `/companies/{id}/branches/{branchId}` | PATCH | Активировать / деактивировать филиал |

**Флоу настройки новой компании:**
```
1. Frontend → GET /companies/search?name=Кофейня+Уют&sources=2gis,yandex
   Web API → POST /api/collection-tasks/search к Parser Service
   Parser Service → возвращает список найденных карточек
   Web API → отдаёт список фронтенду (пользователь выбирает точки)

2. Frontend → POST /companies
   {
     "name": "Кофейня Уют",
     "branches": [
       { "source": "2gis",   "externalId": "...", "externalUrl": "https://...", "name": "...", "address": "..." },
       { "source": "yandex", "externalId": "...", "externalUrl": "https://...", "name": "...", "address": "..." }
     ]
   }
   Web API → INSERT companies + company_branches → { companyId }
```

### Анализ и прогресс

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/companies/{companyId}/analyses` | GET | История анализов компании |
| `/companies/{companyId}/analyses` | POST | Запуск нового анализа |
| `/analyses/{jobId}/progress` | GET | Статус pipeline во время выполнения (polling) — Web API вызывает Processing Gateway: `GET http://processing-gateway/api/analyses/{jobId}/status` |
| `/companies/{companyId}/dashboard` | GET | Дашборд компании (последний анализ + timeseries) |
| `/companies/{companyId}/dashboard?jobId={id}` | GET | Дашборд конкретного исторического анализа |

**Ключевое решение: дашборд по `companyId`, не `jobId`:**
- `/companies/{companyId}/dashboard` — всегда показывает последний `analysis_snapshot` + накопленный `metric_timeseries`
- Подходит и для разового анализа, и для мониторинга (данные накапливаются без смены URL)
- `jobId` используется только для progress screen и опционального просмотра конкретного исторического среза

**Что Web API делает при GET /companies/{companyId}/dashboard:**
```
Web API:
  → проверяет JWT, проверяет что companyId принадлежит userId
  → _analyticsModule.GetLatestSnapshotAsync(companyId)    [прямой вызов модуля]
  → _analyticsModule.GetTimeseriesAsync(companyId, ...)   [прямой вызов модуля]
  → _analyticsModule.GetTopicsAsync(latestJobId)          [прямой вызов модуля]
  → агрегирует → возвращает фронтенду единый ответ
```

Analytics, Reports и Notifications — модули внутри Web API процесса (ADR-011).
Вызовы между ними — прямые вызовы C#-интерфейсов, не HTTP.

### Мониторинги

| Endpoint | Метод | Описание |
|----------|-------|----------|
| `/monitoring` | GET / POST | Список мониторингов / создание + регистрация Hangfire job |
| `/monitoring/{id}` | GET / PATCH / DELETE | Управление |
| `/monitoring/{id}/pause` | POST | Пауза → `RecurringJob.RemoveIfExists(...)` |
| `/monitoring/{id}/resume` | POST | Возобновление → `RecurringJob.AddOrUpdate(...)` |
| `/monitoring/{id}/trigger` | POST | Ручной запуск цикла сбора |

### Администратор

| Endpoint | Метод | Роль | Описание |
|----------|-------|------|----------|
| `/admin/users` | GET | Admin | Список всех пользователей |
| `/admin/users/{id}` | PATCH | Admin | Изменение пользователя |
| `/admin/users/{id}/block` | POST | Admin | Блокировка |
| `/admin/users/{id}/unblock` | POST | Admin | Разблокировка |
| `/admin/logs` | GET | Admin | Проброс к Seq API (поиск по полям) |

Hangfire Dashboard (`/hangfire`) также ограничен ролью Admin через `HangfireAdminAuthFilter`.

### Что Web API НЕ делает

- **Не хранит отзывы** — это Processing Gateway (ADR-002)
- **Не считает агрегаты** — это Analytics-модуль (внутри Web API, ADR-003, ADR-011)
- **Не генерирует PDF** — это Reports-модуль (внутри Web API, ADR-007, ADR-011)
- **Не отправляет Telegram-сообщения** — это Notifications-модуль (внутри Web API, ADR-011)
- **Не запускает парсинг** — публикует команду в брокер, Processing Gateway оркестрирует

### Данные в PostgreSQL (схема Web API)

Identity-таблицы создаются через `dotnet ef migrations`.
Дополнительные таблицы Web API:

```sql
-- Компании пользователя
CREATE TABLE companies (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID NOT NULL REFERENCES "AspNetUsers"(Id),
    name        VARCHAR(300) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Филиалы компании в конкретном источнике
-- Заполняются после того, как пользователь выбрал точки из результатов поиска Parser Service
CREATE TABLE company_branches (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    source          VARCHAR(50) NOT NULL,       -- '2gis', 'yandex', 'google'
    external_id     VARCHAR(500) NOT NULL,      -- ID карточки в источнике
    external_url    VARCHAR(2000),              -- прямая ссылка на карточку
    name            VARCHAR(300),               -- "Кофейня Уют на Ленина, 12"
    address         VARCHAR(500),
    is_active       BOOLEAN NOT NULL DEFAULT true,  -- пользователь может отключить филиал

    UNIQUE (company_id, source, external_id)
);

CREATE INDEX idx_branches_company ON company_branches (company_id);

-- История запусков анализа (Web API владеет записью о намерении пользователя)
-- Статус pipeline хранит Processing Gateway в своей таблице analysis_jobs (processing_db)
-- Web API получает статус через HTTP: GET http://processing-gateway/api/analyses/{jobId}/status
-- Прямой read из processing_db НЕ используется — у сервисов разные БД-инстансы (ADR-011)
CREATE TABLE analysis_requests (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),  -- = jobId
    company_id      UUID NOT NULL REFERENCES companies(id),
    user_id         UUID NOT NULL REFERENCES "AspNetUsers"(Id),
    period_from     TIMESTAMPTZ NOT NULL,
    period_to       TIMESTAMPTZ NOT NULL,
    branch_ids      JSONB NOT NULL,     -- [uuid, uuid, ...] — выбранные филиалы
    sources         JSONB NOT NULL,     -- ["2gis", "yandex"]
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_analysis_requests_company ON analysis_requests (company_id, created_at DESC);

-- Мониторинги (уже описаны в ADR-005, владелец Web API)
CREATE TABLE monitoring_configs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id      UUID NOT NULL REFERENCES companies(id),
    user_id         UUID NOT NULL REFERENCES "AspNetUsers"(Id),
    sources         JSONB NOT NULL,
    branch_ids      JSONB NOT NULL,
    cron_schedule   VARCHAR(100) NOT NULL,
    status          VARCHAR(20) NOT NULL,       -- 'active', 'paused', 'error'
    last_run_at     TIMESTAMPTZ,
    last_run_status VARCHAR(20),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Что идёт в `StartAnalysisCommand` (данные из `company_branches`):**
```csharp
new StartAnalysisCommand {
    JobId      = analysisRequest.Id,
    CompanyId  = company.Id,
    UserId     = userId,
    PeriodFrom = request.PeriodFrom,
    PeriodTo   = request.PeriodTo,
    Branches   = branches.Select(b => new BranchTarget {
        BranchId    = b.Id,
        Source      = b.Source,
        ExternalId  = b.ExternalId,
        ExternalUrl = b.ExternalUrl
    }).ToList()
}
// Processing Gateway передаёт ExternalId/ExternalUrl в Parser — повторный поиск не нужен
```

## Consequences

**Плюсы:**
- ASP.NET Core Identity — стандарт .NET, полная поддержка EF Core + PostgreSQL,
  нет сторонних зависимостей
- JWT stateless — Web API масштабируется без shared session store
- Refresh в httpOnly cookie — XSS не может украсть долгоживущий токен
- Два уровня авторизации (роль в JWT + валидация на сервере) — UI-ограничения не являются защитой
- Identity управляет паролями, хешированием, lockout — не пишем это вручную

**Минусы / риски:**
- JWT не отзывается до истечения (15 мин). При блокировке пользователя — refresh будет отклонён,
  но текущий access token ещё валиден до 15 мин. Приемлемо для MVP
- Refresh token rotation нужно реализовать вручную (Identity не делает это из коробки) —
  стандартная задача, ~50 строк кода
- `SameSite=Strict` для refresh cookie ломает OAuth redirect flows — не проблема, OAuth не планируется

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Lockout после N неудачных попыток логина — включить Identity lockout или кастомный rate limit | При реализации auth |
| Email verification при регистрации (сейчас — без подтверждения) | При реализации |
| Смена пароля / forgot password (SMTP или только Telegram) | При реализации |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Keycloak / IdentityServer | Дополнительный сервис для деплоя и поддержки; избыточен при двух ролях и одном клиенте без SSO |
| Только JWT в localStorage | XSS-уязвимость: JS может прочитать и украсть долгоживущий токен |
| Session-based auth (cookie + Redis) | Требует Redis для горизонтального масштабирования; потеря stateless-преимуществ |
| Cookie-only (без JWT) | Сложнее с CORS при SPA на отдельном домене; CSRF mitigation сложнее |
