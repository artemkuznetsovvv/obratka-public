---
title: "Обратка — Архитектура платформы"
subtitle: "Компоненты системы Обратка"
date: 2026-04-03
lang: ru
toc: true
toc-depth: 2
geometry: "margin=2.5cm"
fontsize: 11pt
header-includes:
  - \usepackage{longtable}
  - \usepackage{booktabs}
---

# ADR для системы "Обратка"

## Назначение

«Обратка» — веб-платформа для сбора и анализа клиентских отзывов с карт
(Google Maps, 2GIS, Яндекс.Карты). Система парсит отзывы, отправляет их во
внешний LLM-сервис (чёрный ящик, наш контракт), получает обратно
структурированные результаты (тональность, темы, фейки, рекомендации),
хранит их, показывает на дашборде, формирует PDF-отчёты и отправляет
уведомления через Telegram-бот.

**Что НЕ является нашей ответственностью:** логика LLM-пайплайна
(язык → спам → фейки → тональность → темы). Это делает внешний сервис.
Наша задача — отправить отзывы туда и корректно обработать ответ.

## Доменная модель

| Сущность | Описание |
|----------|----------|
| **Компания** | Бизнес-объект, по которому запускается анализ |
| **Филиал / Точка** | Конкретная карточка компании в источнике (одна или несколько) |
| **Источник** | Площадка: 2GIS, Яндекс.Карты, Google Maps, Отзовики (опц.) |
| **Отзыв** | Единица данных: текст, дата, источник, звёзды, привязка к филиалу |
| **Анализ** | Сессия сбора + обработки: разовый или live-цикл |
| **Результат анализа** | Данные от LLM: тональность, темы, фейк-статус, спам, рекомендации |
| **Мониторинг** | Настроенный регулярный сбор по компании (статус: активен / пауза / ошибка) |

## Роли

| Роль | Возможности |
|------|-------------|
| **Клиент (User)** | Регистрация/вход, создание компании, запуск анализа, просмотр дашборда, скачивание PDF, настройка live-мониторинга, личный кабинет, управление уведомлениями |
| **Администратор (Admin)** | Просмотр списка пользователей, редактирование, блокировка, просмотр логов ошибок |

## Режимы работы

1. **Разовый анализ** — пользователь нажимает «Запустить анализ», система собирает
   отзывы за выбранный период, отправляет в LLM, показывает результаты на дашборде.
2. **Live-мониторинг** — регулярный сбор только новых отзывов по расписанию (cron);
   обновляет агрегаты дашборда; отправляет итог в Telegram-бот.
3. **Ручное обновление** — кнопка «Обновить вручную» внутри live-мониторинга.

## Масштаб MVP

| Параметр | Значение |
|----------|----------|
| Компаний | 10–100 |
| Отзывов на компанию | 600–1 200 |
| Итого отзывов | до ~120 000 |
| Хранение сырых отзывов | бессрочно |
| Нагрузка | низкая, но архитектура допускает горизонтальное масштабирование |

---

# Архитектура развёртывания

## Deployable units (MVP)

| Сервис / Модуль | Тип | Ответственность | Стек | БД |
|-----------------|-----|-----------------|------|----|
| **Web API** | Deployable unit | BFF, auth, маршрутизация, Hangfire | ASP.NET Core, Identity, Hangfire, MassTransit | PostgreSQL `webapi_db` |
| ↳ *[Module] Analytics* | Модуль | Расчёт агрегатов, KPI, timeseries | EF Core | Читает `processing_db`; пишет в `webapi_db` |
| ↳ *[Module] Reports* | Модуль | Генерация PDF | QuestPDF, SkiaSharp | Читает `webapi_db` |
| ↳ *[Module] Notifications* | Модуль | Telegram-бот | Telegram.Bot | — |
| **Processing Gateway** | Deployable unit | Оркестрация pipeline: Parser → S3 → LLM → БД | ASP.NET Core, MassTransit, EF Core | PostgreSQL `processing_db` |
| **Parser Service** | Deployable unit | Сбор отзывов; плагинная архитектура | ASP.NET Core, Playwright | SQLite (статусы задач) |
| **Frontend (SPA)** | Deployable unit | UI: дашборд, прогресс, мониторинг, admin | React, TypeScript, Vite, shadcn/ui | — |

## Инфраструктурные компоненты

| Компонент | Образ | Назначение |
|-----------|-------|------------|
| PostgreSQL (webapi_db) | postgres | БД Web API: пользователи, компании, аналитика, Hangfire |
| PostgreSQL (processing_db) | postgres | БД Processing Gateway: отзывы, LLM-результаты, статусы |
| RabbitMQ | rabbitmq:3-management | Брокер сообщений (MassTransit) |
| MinIO | minio/minio | S3-совместимое blob-хранилище |
| Seq | datalust/seq | Централизованные логи |

**Итого контейнеров в docker-compose: 9** (4 сервиса + 2 PostgreSQL + RabbitMQ + MinIO + Seq).

---

# Описания сервисов

## Web API

### Назначение

Web API — единственная точка входа для Frontend SPA (BFF-паттерн). Проверяет JWT,
проверяет права доступа к ресурсам, маршрутизирует запросы. Самостоятельно не парсит,
не считает агрегаты, не генерирует PDF — делегирует встроенным модулям или публикует команды в брокер.
Включает три встроенных модуля: Analytics, Reports, Notifications.

### Инфраструктура

| Ресурс | Использование |
|--------|--------------|
| PostgreSQL `webapi_db` | Основная БД: пользователи, компании, аналитика, расписания |
| PostgreSQL `processing_db` | Read-only: Analytics-модуль читает отзывы и LLM-результаты |
| RabbitMQ | Публикует команды запуска анализа; подписан на события завершения |
| MinIO `obratka-reports` | Хранение сгенерированных PDF |
| Seq | Отправка структурированных логов |

### Компоненты

- **ASP.NET Core (HTTP / BFF)** — аутентификация (JWT Bearer 15 мин + Refresh Token 7 дней), авторизация по ролям `User` / `Admin`, маршрутизация к модулям. Группы эндпоинтов: Auth, Companies, Analyses, Dashboard, Reports, Monitoring, Profile, Admin.
- **Hangfire** — планировщик recurring-задач внутри процесса, хранилище в `webapi_db`. Запускает cron мониторингов и еженедельную генерацию PDF.
- **MassTransit** — публикует `StartAnalysisCommand`, `StartMonitoringCycleCommand`, `AggregatesReadyEvent`; подписан на `AnalysisCompletedEvent`, `MonitoringCycleCompletedEvent`.
- **[Module] Analytics** — расчёт агрегатов после завершения анализа; пишет `analysis_snapshots`, `metric_timeseries`, `branch_snapshots`, `topic_stats` в `webapi_db`.
- **[Module] Reports** — генерирует PDF (QuestPDF + SkiaSharp); сохраняет в MinIO `obratka-reports`.
- **[Module] Notifications** — отправляет Telegram-уведомления пользователям и администратору.

---

## Processing Gateway

### Назначение

Processing Gateway — оркестратор pipeline анализа и единственная точка интеграции с внешним
LLM-сервисом. Получает команды от Web API → запускает сбор (Parser Service) → сохраняет сырые
данные → отправляет в LLM → сохраняет результаты → уведомляет Web API о завершении.

### Инфраструктура

| Ресурс | Использование |
|--------|--------------|
| PostgreSQL `processing_db` | Основная БД: отзывы, LLM-результаты, статусы задач |
| RabbitMQ | Подписан на команды старта; публикует события завершения и LLM-запросы |
| MinIO `obratka-jobs` | Читает `raw/*.json` от Parser; пишет `input.json` для LLM; читает `output.json` |
| Seq | Отправка структурированных логов |

### Компоненты

- **HTTP Status API** — внутренний эндпоинт `GET /api/analyses/{jobId}/status` для progress screen во Frontend.
- **MassTransit** — подписан на `StartAnalysisCommand`, `StartMonitoringCycleCommand`, `AggregatesReadyEvent`, ответ LLM; публикует `AnalysisCompletedEvent`, `MonitoringCycleCompletedEvent`, запрос к LLM.
- **Parser poller** — запускает сбор параллельно по источникам, опрашивает статус каждые 3–5 сек, при завершении скачивает `raw/{source}.json` из S3 и вставляет отзывы в БД.
- **LLM pipeline** — языковая классификация отзывов → загрузка `input.json` в S3 → публикация запроса LLM через RabbitMQ (claim-check) → получение `output.json` → сохранение результатов.
- **EF Core** — запись в `processing_db`: таблицы `reviews`, `review_llm_results`, `analysis_jobs`.

**Статусная машина** `analysis_jobs.status`:
```
pending → collecting → language_detection → sent_to_llm → computing_aggregates → completed / partial (?) / failed
```

---

## Parser Service

### Назначение

Parser Service — stateless REST-воркер для сбора отзывов. Единственная ответственность:
получить задачу, выполнить сбор, записать результат в S3, сообщить статус. Не хранит
собранные отзывы — они уходят в S3. Изолирует браузерную автоматизацию,
прокси-ротацию и обход антибота, либо внешний API для их получения.

### Инфраструктура

| Ресурс | Использование |
|--------|--------------|
| SQLite `/data/tasks.db` | Хранение статусов активных задач (переживает рестарт контейнера) |
| MinIO `obratka-jobs` | Запись `raw/{source}.json` с собранными отзывами |
| Seq | Отправка структурированных логов |

### Компоненты

- **REST API** — три эндпоинта: поиск карточек компании, запуск сбора, получение статуса задачи.
- **Плагинная архитектура (IReviewSourcePlugin)** — отдельный плагин на каждый источник: `TwoGisPlugin`, `YandexMapsPlugin`, `GoogleMapsPlugin`, `OtzovikPlugin` (опц.). Добавление нового источника = новый плагин без изменения остального кода.
- **Антибот-инфраструктура** — общая для всех плагинов: пул браузеров Playwright (Chromium), ротация прокси, stealth-режим, rate limiting per источник.
- **S3 Result Storage** — Parser только пишет в `s3://obratka-jobs/{job_id}/raw/{source}.json`; читает Processing Gateway.

---

## Frontend (SPA)

### Назначение

Frontend — единственный UI системы. Общается **только с Web API** (JWT в Authorization header).
Все downstream-сервисы (Processing Gateway, Parser Service) недоступны снаружи.

### Инфраструктура

| Ресурс | Использование |
|--------|--------------|
| Web API | HTTP REST + JWT — все запросы |

### Стек

| Аспект | Библиотека |
|--------|-----------|
| Фреймворк | React + TypeScript |
| Сборщик | Vite |
| Компоненты UI | shadcn/ui (Radix UI + Tailwind CSS) |
| Data fetching | TanStack Query (polling, cache, optimistic updates) |
| Графики | Recharts |
| Роутинг | React Router v6 |
| API-клиент | OpenAPI-generated TypeScript client |

### Экраны

| Экран | Описание |
|-------|----------|
| Регистрация / Вход | Auth форма; JWT в памяти, refresh — httpOnly cookie |
| Настройка компании | Поиск карточек по источникам, выбор филиалов |
| Запуск анализа | Выбор периода и филиалов |
| Progress screen | Polling каждые 3 сек; отображает этапы обработки и статусы источников |
| Дашборд | 5 KPI, тональность, темы, фильтры (период / источник / филиал / тема / тональность / звёзды) |
| Мониторинги | Список, статусы, пауза / возобновление / ручной запуск |
| Скачивание PDF | Запрос генерации отчёта и скачивание |
| Личный кабинет | Профиль, привязка Telegram, настройки уведомлений |
| Административная панель | Список пользователей, блокировка, просмотр логов (Seq proxy) |

---

# Сводка архитектурных решений (ADR)

| ADR | Заголовок | Ключевое решение | Делам в MVP ? |
|-----|-----------|-----------------|--------|
| 001 | Декомпозиция Parser Service | Один сервис с плагинами per source; stateless REST-воркер; S3 claim-check | Да |
| 002 | БД для сырых отзывов и LLM-результатов | PostgreSQL `processing_db`; владелец — Processing Gateway | Да |
| 003 | БД для аналитических метрик | PostgreSQL `webapi_db`; pre-computed агрегаты; `metric_timeseries` | Да |
| 004 | Транспорт к внешнему LLM | MassTransit + RabbitMQ + claim-check через MinIO (S3) | Да |
| 005 | Планировщик | Hangfire + PostgreSQL storage; в процессе Web API | Да |
| 006 | Фронтенд | React + TypeScript + Vite + shadcn/ui + TanStack Query + Recharts | Да |
| 007 | PDF-генерация | QuestPDF + SkiaSharp; серверная, без браузера | Да |
| 008 | Централизованное логирование | Seq (MVP) + Serilog; correlation ID; путь к ELK | Да |
| 009 | Web API — аутентификация | ASP.NET Core Identity + JWT Bearer + Refresh Token (httpOnly cookie) | Да |
| 010 | Observability (post-MVP) | OpenTelemetry → Prometheus → Grafana; инструментация с MVP | Нет, после MVP |
| 011 | MVP Service Decomposition | Modular Monolith: 3 deployable unit; отдельные БД; модульные интерфейсы | - |

---

# Технологический стек

| Категория | Технология | ADR |
|-----------|-----------|-----|
| Язык / рантайм | C# / .NET 8 | — |
| Фронтенд | React + TypeScript + Vite + (Для дашбордов LLM-посоветовала всякие: shadcn/ui + TanStack Query + Recharts ) | 006 |
| Аутентификация | ASP.NET Core Identity + JWT + httpOnly Refresh | 009 |
| ORM | EF Core (основные), Dapper (при необходимости low-level SQL) | — |
| Брокер | MassTransit + RabbitMQ | 004 |
| Blob-хранилище | MinIO (S3-совместимое, self-hosted; prod — Selectel / Yandex Object Storage) | 004 |
| БД | PostgreSQL (2 инстанса) | 002, 003 |
| Планировщик | Hangfire + PostgreSQL storage | 005 |
| PDF-генерация | QuestPDF + SkiaSharp (серверная) | 007 |
| Логирование | Serilog → Seq | 008 |
| Метрики (post-MVP) | OpenTelemetry → Prometheus → Grafana | 010 |
| Парсинг | Playwright (browser pool, proxy rotation, stealth) / Внешние API | 001 |
| Telegram | Telegram.Bot | 011 |

---

# Функциональные требования (кратко)

## KPI дашборда

1. Количество отзывов за период
2. Средний рейтинг (звёзды)
3. Распределение тональности (по 5 уровням)
4. Индекс лояльности (NPS-подобный): `(промоутеры − детракторы) × 100%`
5. Доля подозрительных + фейковых отзывов

## Telegram-уведомления

**Для пользователя (live-мониторинг):**
- «Обновление выполнено» + количество новых отзывов
- «Резкий рост негатива» (доля негативных выросла на ≥15 п.п. vs предыдущий период И ≥20 отзывов)
- «Источник недоступен / частичное обновление»

**Для администратора (всегда):**
- Ошибка сбора, анализа или генерации отчёта
- Превышение допустимого числа retry

## Логирование

- **Инструмент:** Serilog → Seq (MVP).
- **Каждая запись:** время, сервис, инициатор, компания/анализ, результат, длительность, correlation ID.
- **Retention:** 30–90 дней.
- **Доступ:** только администратор.

## Устойчивость

- При недоступности источника — retry минимум 3 раза, фиксация в логах.
- Статус live-мониторинга: `успешно / частично / ошибка`.
- Система всегда показывает статус и этап обработки (progress screen).
- При критических ошибках — уведомление администратору в Telegram.

# Функциональные требования

## Устойчивость
- Система должна поддерживать обработку 600-1200 отзывов (входит в MVP)
- Система должна быть легко масштабируемой в случае увеличения числа клиентов (входит в MVP, в конкретных ADR описаны способы перехода)

## SLO
- В рамках MVP SLO не предусмотрено, но описано в ADR-010
