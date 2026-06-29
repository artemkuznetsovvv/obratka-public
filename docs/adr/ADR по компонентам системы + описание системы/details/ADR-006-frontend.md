# ADR-006: Фронтенд — стек и инструменты

## Context

SPA для платформы «Обратка»: дашборд с KPI-карточками, трендовыми графиками,
фильтрами, экраном прогресса, разделом мониторингов, личным кабинетом и
административной панелью.

**Ключевое ограничение:** фронтенд будет разрабатываться преимущественно
с помощью Claude Code («вайбкодинг»). Это смещает выбор в сторону технологий
с наибольшим объёмом обучающих данных у LLM, зрелой экосистемой и
максимальным количеством примеров в открытом доступе.

**Бэкенд:** .NET 8 Web API (REST + JSON). Фронтенд — отдельный SPA,
общается с API через HTTP.

## Decision

### React + TypeScript + Vite

**React** — наибольший объём обучающих данных среди UI-фреймворков.
Claude Code генерирует React-компоненты надёжнее, чем Vue или Blazor.
Огромная экосистема, стандартные паттерны (хуки, компоненты) хорошо изучены AI.

**TypeScript** — строгие типы снижают количество ошибок при генерации кода AI:
интерфейсы DTO совпадают с бэкенд-контрактами, IDE подсвечивает несоответствия
до запуска. Особенно важно при вайбкодинге, где объём ручной ревью-проверки
минимален.

**Vite** — стандартный современный bundler для React. Быстрый HMR, минимальная
конфигурация, хорошо знаком Claude Code.

```
npm create vite@latest frontend -- --template react-ts
```

### UI: shadcn/ui

Библиотека компонентов, идеально подходящая для вайбкодинга:
- Компоненты копируются в проект как исходный код (не npm-пакет) → можно менять напрямую
- Claude Code знает каждый компонент shadcn: `Button`, `Card`, `DataTable`, `Select`,
  `DatePickerWithRange`, `Tabs`, `Badge`, `Dialog`, `Skeleton` и т.д.
- Построена на Radix UI (accessibility из коробки) + Tailwind CSS
- Огромное количество примеров и сниппетов в интернете

```bash
npx shadcn@latest init
npx shadcn@latest add card table button badge tabs dialog
```

### Стилизация: Tailwind CSS

- Идёт в комплекте с shadcn/ui
- Utility-first: AI генерирует inline-классы без необходимости придумывать имена
- Единый дизайн-язык без написания CSS вручную

### Серверное состояние: TanStack Query (React Query)

Управление данными от API: кэш, refetch, stale-time, polling.

```tsx
// Прогресс-экран: polling каждые 3 сек до завершения
const { data: job } = useQuery({
  queryKey: ['job', jobId],
  queryFn: () => api.getJobStatus(jobId),
  refetchInterval: (data) =>
    data?.status === 'completed' ? false : 3000,
});

// Дашборд: данные с кэшем
const { data: snapshot } = useQuery({
  queryKey: ['snapshot', analysisJobId],
  queryFn: () => api.getSnapshot(analysisJobId),
  staleTime: 5 * 60 * 1000,  // 5 минут
});
```

TanStack Query решает прогресс-экран без написания polling-логики вручную —
`refetchInterval` с условием завершения.

### Графики: Recharts

Самая популярная библиотека графиков для React. Claude Code хорошо знает API.

```tsx
// NPS по неделям
<LineChart data={timeseries}>
  <XAxis dataKey="period_week" />
  <YAxis domain={[-100, 100]} />
  <Line dataKey="nps" stroke="#6366f1" />
  <Tooltip />
</LineChart>

// Распределение тональности
<BarChart data={sentimentDist}>
  <Bar dataKey="count" fill="#6366f1" />
</BarChart>
```

### HTTP-клиент: Axios + сгенерированный API-клиент

```bash
# Генерация TypeScript-клиента из OpenAPI-схемы .NET бэкенда
npx openapi-typescript-codegen --input http://localhost:5000/swagger/v1/swagger.json \
  --output src/api --client axios
```

Типизированный клиент из Swagger-схемы → AI знает точные типы запросов/ответов,
нет ручного написания интерфейсов.

### Роутинг: React Router v6

Стандартный выбор, хорошо знаком Claude Code.

```
/                     → редирект на /dashboard или /login
/login                → страница входа
/register             → регистрация
/dashboard/:jobId     → дашборд анализа
/analyses/new         → запуск нового анализа
/analyses/:jobId/progress → прогресс-экран
/monitoring           → список мониторингов
/monitoring/:id       → детали мониторинга
/profile              → личный кабинет
/admin/users          → список пользователей (Admin)
/admin/logs           → логи ошибок (Admin)
```

### Структура проекта

```
frontend/
├── src/
│   ├── api/           ← сгенерированный клиент из OpenAPI
│   ├── components/
│   │   ├── ui/        ← shadcn/ui компоненты (скопированы)
│   │   ├── dashboard/ ← KpiCard, SentimentChart, TopicList, NpsTrend...
│   │   ├── monitoring/
│   │   └── shared/    ← Layout, Navbar, ProgressScreen...
│   ├── pages/         ← страницы по роутам
│   ├── hooks/         ← useAnalysis, useMonitoring, useAuth...
│   └── lib/           ← утилиты, форматтеры
├── index.html
└── vite.config.ts
```

## Consequences

**Плюсы:**
- Максимальная совместимость с Claude Code: React + shadcn — наиболее изученная
  комбинация среди LLM
- TypeScript ловит ошибки типов до запуска — критично при вайбкодинге
- TanStack Query решает прогресс-экран и кэш дашборда без ручного кода
- Сгенерированный API-клиент из OpenAPI — нет расхождений с бэкенд-контрактами
- shadcn/ui: готовые компоненты (таблицы, фильтры, date picker, модалки) без дизайна вручную
- Vite: быстрый старт, HMR, стандартный тулчейн

**Минусы / риски:**
- Tailwind CSS: при сложной кастомизации дизайна может потребоваться ручная работа
- SPA без SSR: SEO не нужен (B2B платформа), поэтому Next.js избыточен
- Объём бойлерплейта при большом числе страниц — покрывается shadcn и
  генерированным клиентом

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Деплой фронтенда: отдельный nginx / раздача статики из Web API | При настройке CI/CD |
| Auth: JWT в localStorage vs httpOnly cookie | При реализации auth |
| i18n: только русский на MVP или сразу мультиязычность | При старте разработки фронта |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Blazor (Server / WASM) | Меньше обучающих данных у LLM, меньше экосистемы; вайбкодить сложнее. Оправдан если команда только C# без JS — здесь не наш случай |
| Vue 3 | Хорошая альтернатива, но меньше примеров и обучающих данных чем у React; shadcn/ui нет для Vue |
| Next.js вместо Vite | SSR избыточен для B2B SPA; добавляет сложность серверного рендеринга без выигрыша |
| MUI (Material UI) | Хуже подходит для вайбкодинга: сложная система тем, меньше примеров shadcn-стиля |
