# ADR-007: PDF-генерация отчётов

> **Deployment context (ADR-011):** Reports реализован как **модуль внутри Web API**, не как отдельный сервис.
> Интерфейс: `IReportsModule`. Вызывается напрямую из Web API (Hangfire job или MassTransit consumer) — без брокера.

## Context

Reports-модуль (Web API) генерирует PDF-отчёты в двух сценариях:
1. **По запросу пользователя** — кнопка «Скачать PDF» на дашборде
2. **Автономно** — еженедельная генерация (Hangfire job → прямой вызов `IReportsModule`)

Второй сценарий исключает любой подход, зависящий от браузера пользователя.
PDF генерируется полностью на сервере без участия фронтенда.

**Состав PDF (из ТЗ):**
1. Параметры анализа (компания, источники, период, филиалы)
2. 5 KPI (количество отзывов, средний рейтинг, NPS, тональность, доля фейков)
3. Распределение тональности (5 уровней) — **график**
4. Темы и болевые точки — **график + таблица**
5. Доля фейковых / подозрительных отзывов
6. Рекомендации (стратегические / тактические / коммуникационные)
7. Топ позитивных / негативных отзывов (опционально)

## Decision

### QuestPDF + SkiaSharp

**QuestPDF** — layout-движок: секции, колонки, таблицы, текст, встроенные изображения.
**SkiaSharp** — 2D-графика: рендерит графики в PNG-байты, которые QuestPDF вставляет в документ.

Оба пакета — чистый .NET, без внешних сервисов и браузеров.

```
NuGet: QuestPDF, SkiaSharp
```

**Почему QuestPDF, а не iTextSharp:**
iTextSharp (iText 7) распространяется под AGPL — при использовании в закрытом
коммерческом продукте требует открытия исходного кода всего приложения
или коммерческой лицензии ($$$). QuestPDF — MIT / Community license (бесплатно
для компаний с выручкой до $1M, что покрывает MVP и ранний рост).

**Почему не Gotenberg / PuppeteerSharp:**
Оба требуют Chromium в инфраструктуре. QuestPDF + SkiaSharp — ноль новых
Docker-контейнеров, всё в одном .NET-процессе Web API.

### Графики через SkiaSharp

SkiaSharp — .NET-обёртка над Google Skia (движок Chrome/Android).
Рисует в `SKBitmap`, возвращает PNG-байты, QuestPDF вставляет их через `Image`.

**Графики в отчёте:**

| График | Тип | Источник данных |
|--------|-----|----------------|
| Распределение тональности | Горизонтальный bar chart (5 полос) | `analysis_snapshots.sentiment_dist` |
| Динамика NPS по неделям | Line chart | `metric_timeseries` |
| Топ-темы по количеству отзывов | Горизонтальный bar chart | `topic_stats` |
| Тональность по теме | Stacked bar | `topic_stats.sentiment_dist` |

```csharp
// Пример: рендер bar chart тональности через SkiaSharp
public static byte[] RenderSentimentChart(SentimentDist dist, int width = 600, int height = 200)
{
    using var bitmap = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);

    var colors = new[] {
        SKColor.Parse("#ef4444"),   // very_negative
        SKColor.Parse("#f97316"),   // negative
        SKColor.Parse("#a3a3a3"),   // neutral
        SKColor.Parse("#86efac"),   // positive
        SKColor.Parse("#22c55e"),   // very_positive
    };

    var values = dist.ToArray();  // [count_vn, count_n, count_neu, count_p, count_vp]
    var total = values.Sum();
    var barHeight = 28;
    var gap = 8;
    var labelWidth = 120;

    for (int i = 0; i < values.Length; i++)
    {
        var ratio = total > 0 ? (float)values[i] / total : 0f;
        var barWidth = (int)((width - labelWidth - 20) * ratio);
        var y = i * (barHeight + gap) + 20;

        using var paint = new SKPaint { Color = colors[i], IsAntialias = true };
        canvas.DrawRoundRect(new SKRoundRect(
            new SKRect(labelWidth, y, labelWidth + barWidth, y + barHeight), 4), paint);

        // подпись + процент
        using var textPaint = new SKPaint {
            Color = SKColors.Black, TextSize = 13, IsAntialias = true
        };
        canvas.DrawText($"{dist.Labels[i]}  {ratio:P0}", 0, y + barHeight - 6, textPaint);
    }

    using var image = SKImage.FromBitmap(bitmap);
    return image.Encode(SKEncodedImageFormat.Png, 95).ToArray();
}
```

### Структура PDF (QuestPDF)

```csharp
Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11));

        page.Header().Element(ComposeHeader);
        page.Content().Element(ComposeContent);
        page.Footer().AlignCenter().Text(x => {
            x.CurrentPageNumber();
            x.Span(" / ");
            x.TotalPages();
        });
    });
});

void ComposeContent(IContainer container)
{
    container.Column(col =>
    {
        col.Item().Element(ComposeParameters);   // 1. Параметры анализа
        col.Item().Element(ComposeKpis);          // 2. KPI-карточки
        col.Item().Element(ComposeSentiment);     // 3. Тональность + график
        col.Item().Element(ComposeTopics);        // 4. Темы и болевые точки
        col.Item().Element(ComposeFakeStats);     // 5. Фейки / спам
        col.Item().Element(ComposeRecommendations); // 6. Рекомендации
        col.Item().Element(ComposeTopReviews);    // 7. Топ отзывов (опц.)
    });
}
```

### Поток генерации

```
По запросу пользователя:
  Web API (HTTP handler):
  → прямой вызов IReportsModule.GenerateReportAsync(analysisJobId, companyId)

Reports-модуль:
  → прямой вызов IAnalyticsModule: GetLatestSnapshotAsync + GetTimeseriesAsync + GetTopicsAsync
    (все данные из webapi_db — нет cross-service запросов)
  → SkiaSharp рендерит графики → PNG байты
  → QuestPDF собирает документ, вставляет графики
  → PDF байты → PUT s3://obratka-reports/{companyId}/{jobId}/report.pdf
  → возвращает S3-ключ вызывающему коду

Web API (по запросу пользователя):
  → GET PDF из S3 → возвращает FileResult (Content-Type: application/pdf) клиенту
    (клиент скачивает файл напрямую через браузер)

Автономно (еженедельно):
  Hangfire job (внутри Web API):
  → IReportsModule.GenerateReportAsync(...)
  → INotificationsModule.SendReportReadyAsync(reportUrl, userId)
    → Telegram: sendDocument(chatId, pdfBytes)
```

### Лицензирование QuestPDF

| Сценарий | Лицензия |
|----------|----------|
| Выручка компании < $1M/год | Community (бесплатно) |
| Выручка $1M–$5M/год | Professional |
| Выручка > $5M/год | Enterprise |

Для MVP — Community license. При росте — переход на Professional без изменения кода.

## Consequences

**Плюсы:**
- Ноль новых инфраструктурных компонентов — всё в одном .NET-процессе
- QuestPDF: fluent API, хорошая документация, активная разработка
- SkiaSharp: промышленный уровень (движок Chrome), полный контроль над внешним видом графиков
- Генерация не зависит от фронтенда — автономная доставка в Telegram работает всегда
- Быстрая генерация: нет запуска браузера, нет сетевых запросов к фронту

**Минусы / риски:**
- Графики в PDF ≠ пиксельно точная копия Recharts-графиков на дашборде.
  Данные те же, визуальный стиль — схожий, но написан отдельно
- SkiaSharp требует нативных бинарников под каждую ОС (linux-x64, win-x64).
  Решается через `SkiaSharp.NativeAssets.Linux` NuGet-пакет в Docker-образе
- Сложные chart-типы (stacked bar с несколькими сериями) потребуют больше кода,
  чем декларативный Recharts

## Открытые вопросы

| Вопрос | Когда решать |
|--------|-------------|
| Брендирование: логотип, цвета клиента в отчёте | При реализации Reports-модуля |
| Язык отчёта (RU/EN) | При реализации |
| TTL PDF в S3 (presigned URL на 24ч или бессрочно) | При реализации |

## Alternatives Considered

| Вариант | Почему отклонён |
|---------|----------------|
| Playwright (рендер React-фронтенда) | Зависимость от фронтенда: при автономной Telegram-доставке фронтенд недоступен или требует auth-хаков |
| PuppeteerSharp + собственные шаблоны | Chromium в инфраструктуре; тот же результат что QuestPDF+SkiaSharp, но тяжелее |
| Gotenberg | Дополнительный Docker-контейнер; нет выигрыша перед QuestPDF+SkiaSharp при нашем объёме |
| iTextSharp (iText 7) | AGPL-лицензия несовместима с закрытым коммерческим продуктом без дорогой коммерческой лицензии |
