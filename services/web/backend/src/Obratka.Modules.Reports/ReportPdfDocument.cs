using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Obratka.Modules.Reports;

// QuestPDF-документ отчёта. Только рендер модели — никаких расчётов.
// Графики — нативные QuestPDF (цветные бары/таблицы), без SkiaSharp-чартов (MVP, ADR-007 упрощение).
internal sealed class ReportPdfDocument(ReportDocumentModel model) : IDocument
{
    // Палитра под shadcn-тему фронта.
    private const string Ink = "#0f172a";
    private const string Muted = "#64748b";
    private const string Line = "#e2e8f0";
    private const string Panel = "#f8fafc";
    private const string Brand = "#4f46e5";
    private const string Green = "#10b981";
    private const string Slate = "#94a3b8";
    private const string Rose = "#f43f5e";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontFamily(ReportPdfBootstrap.FontFamily).FontSize(10).FontColor(Ink));

            page.Header().Element(ComposeHeader);
            page.Content().Element(ComposeContent);
            page.Footer().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                t.Span("Обратка · ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    private void ComposeHeader(IContainer container)
    {
        container.BorderBottom(1).BorderColor(Line).PaddingBottom(6).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(model.Meta.CompanyName).FontSize(14).Bold().FontColor(Ink);
                col.Item().Text("Отчёт по отзывам · Обратка").FontSize(8).FontColor(Muted);
            });
            row.ConstantItem(160).AlignRight().Text(
                $"Сформирован {model.Meta.GeneratedAt.ToLocalTime():dd.MM.yyyy HH:mm}")
                .FontSize(8).FontColor(Muted);
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.PaddingTop(10).Column(col =>
        {
            col.Spacing(12);

            col.Item().Element(ComposeParams);

            for (var i = 0; i < model.Sections.Count; i++)
            {
                if (i > 0) col.Item().PageBreak();
                var section = model.Sections[i];
                col.Item().Element(c => ComposeSection(c, section));
            }

            if (model.Recommendations.Count > 0)
            {
                col.Item().PageBreak();
                col.Item().Element(ComposeRecommendations);
            }

            if (model.Examples.Positive.Count > 0 || model.Examples.Negative.Count > 0)
            {
                col.Item().PageBreak();
                col.Item().Element(ComposeExamples);
            }
        });
    }

    private void ComposeParams(IContainer container)
    {
        var m = model.Meta;
        container.Column(col =>
        {
            col.Spacing(4);
            col.Item().Text("Параметры анализа").FontSize(16).Bold();

            InfoLine(col, "Компания", m.CompanyName);
            InfoLine(col, "Период", m.PeriodLabel);
            InfoLine(col, "Источники", m.SourceLabels.Count > 0 ? string.Join(", ", m.SourceLabels) : "—");
            InfoLine(col, "Статус", m.StatusLabel);
            foreach (var line in m.AppliedFilterLines)
                InfoLine(col, "Фильтр", line);

            col.Item().PaddingTop(4).Text($"Филиалы ({m.Branches.Count})").SemiBold().FontColor(Ink);
            foreach (var b in m.Branches)
            {
                col.Item().PaddingLeft(8).Text(t =>
                {
                    t.Span(string.IsNullOrWhiteSpace(b.Address) ? (b.Name ?? "Филиал") : b.Address)
                        .FontSize(9).FontColor(Ink);
                    var tail = new List<string>();
                    if (!string.IsNullOrWhiteSpace(b.Address) && !string.IsNullOrWhiteSpace(b.Name)) tail.Add(b.Name!);
                    if (!string.IsNullOrWhiteSpace(b.City)) tail.Add(b.City!);
                    if (tail.Count > 0) t.Span($"  ·  {string.Join(" · ", tail)}").FontSize(8).FontColor(Muted);
                });
            }
        });
    }

    private static void InfoLine(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(row =>
        {
            row.ConstantItem(90).Text(label).FontSize(9).FontColor(Muted);
            row.RelativeItem().Text(value).FontSize(9).FontColor(Ink);
        });
    }

    private void ComposeSection(IContainer container, ReportSection s)
    {
        container.Column(col =>
        {
            col.Spacing(6);

            col.Item().Text(s.Title).FontSize(16).Bold().FontColor(s.IsNetwork ? Brand : Ink);
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
                col.Item().Text(s.Subtitle).FontSize(9).FontColor(Muted);

            // KPI-ряд (≥5 показателей суммарно по отчёту: количество, рейтинг, рекомендуют,
            // пульс + тональность ниже).
            col.Item().PaddingTop(2).Row(row =>
            {
                row.Spacing(8);
                Kpi(row, "Отзывов", s.ReviewCount.Total.ToString("N0", Ru));
                Kpi(row, "Средний рейтинг", s.AverageRating.Total is { } a ? a.ToString("0.0", Ru) : "—");
                Kpi(row, "Рекомендуют", Pct(s.RecommendPercent.Positive, s.RecommendPercent.Total));
                Kpi(row, "Свежий пульс", s.FreshPulse.Index is { } idx ? Signed(idx) : "—");
            });

            // По источникам: количество + средний рейтинг в одну строку.
            if (s.ReviewCount.BySource.Count > 0)
            {
                col.Item().Text(t =>
                {
                    t.Span("По источникам:  ").FontSize(8).FontColor(Muted);
                    var ratingBySource = s.AverageRating.BySource.ToDictionary(x => x.SourceLabel);
                    foreach (var src in s.ReviewCount.BySource)
                    {
                        ratingBySource.TryGetValue(src.SourceLabel, out var r);
                        var rating = r?.Average is { } av ? $" (рейтинг {av.ToString("0.0", Ru)})" : "";
                        t.Span($"{src.SourceLabel} {src.Count.ToString("N0", Ru)}{rating}    ").FontSize(8).FontColor(Ink);
                    }
                });
            }

            ComposeSentiment(col, s.Sentiment);
            ComposeTopics(col, s);
            ComposeFlow(col, s.Flow);
        });
    }

    private static void Kpi(RowDescriptor row, string label, string value)
    {
        row.RelativeItem().Border(1).BorderColor(Line).Background(Panel).Padding(8).Column(c =>
        {
            c.Item().Text(label).FontSize(7).FontColor(Muted);
            c.Item().Text(value).FontSize(16).Bold().FontColor(Ink);
        });
    }

    private void ComposeSentiment(ColumnDescriptor col, ReportSentiment s)
    {
        col.Item().PaddingTop(6).Text("Настроение клиентов").FontSize(11).SemiBold();
        if (s.Total == 0)
        {
            col.Item().Text("Нет размеченных отзывов за выбранный период.").FontSize(9).FontColor(Muted);
            return;
        }

        col.Item().PaddingTop(2).Height(16).Row(row =>
        {
            if (s.Positive > 0) row.RelativeItem(s.Positive).Background(Green);
            if (s.Neutral > 0) row.RelativeItem(s.Neutral).Background(Slate);
            if (s.Negative > 0) row.RelativeItem(s.Negative).Background(Rose);
        });
        col.Item().Text(t =>
        {
            t.Span($"Позитив {Pct(s.Positive, s.Total)}").FontColor(Green).FontSize(9);
            t.Span("   ·   ").FontColor(Muted).FontSize(9);
            t.Span($"Нейтрально {Pct(s.Neutral, s.Total)}").FontColor(Slate).FontSize(9);
            t.Span("   ·   ").FontColor(Muted).FontSize(9);
            t.Span($"Негатив {Pct(s.Negative, s.Total)}").FontColor(Rose).FontSize(9);
            t.Span($"   ({s.Total.ToString("N0", Ru)} с оценкой LLM)").FontColor(Muted).FontSize(8);
        });
    }

    private void ComposeTopics(ColumnDescriptor col, ReportSection s)
    {
        col.Item().PaddingTop(6).Text("О чём говорят чаще всего").FontSize(11).SemiBold();
        if (s.Topics.Count == 0)
        {
            col.Item().Text("Темы не выделены.").FontSize(9).FontColor(Muted);
            return;
        }

        foreach (var t in s.Topics)
        {
            var share = s.TopicsTotalReviews > 0 ? (double)t.ReviewCount / s.TopicsTotalReviews * 100 : 0;
            col.Item().PaddingTop(3).Row(row =>
            {
                row.Spacing(6);
                row.ConstantItem(150).Column(c =>
                {
                    c.Item().Text(t.Topic).SemiBold().FontSize(9).FontColor(Ink);
                    c.Item().Text($"{t.ReviewCount.ToString("N0", Ru)} отз. · {share.ToString("0", Ru)}%")
                        .FontSize(8).FontColor(Muted);
                });
                row.RelativeItem().AlignMiddle().Height(12).Row(bar =>
                {
                    var tot = t.PositiveMentions + t.NegativeMentions;
                    if (tot == 0)
                    {
                        bar.RelativeItem().Background(Line);
                    }
                    else
                    {
                        if (t.PositiveMentions > 0) bar.RelativeItem(t.PositiveMentions).Background(Green);
                        if (t.NegativeMentions > 0) bar.RelativeItem(t.NegativeMentions).Background(Rose);
                    }
                });
                row.ConstantItem(78).AlignMiddle().AlignRight()
                    .Text($"+{t.PositiveMentions.ToString("N0", Ru)} / -{t.NegativeMentions.ToString("N0", Ru)}")
                    .FontSize(8).FontColor(Muted);
            });
        }
    }

    private void ComposeFlow(ColumnDescriptor col, ReportFlow f)
    {
        col.Item().PaddingTop(6).Text(t =>
        {
            t.Span("Поток новых отзывов").FontSize(11).SemiBold();
            t.Span($"   (окно {f.WindowLabel})").FontSize(8).FontColor(Muted);
        });
        col.Item().Text(t =>
        {
            t.Span($"Текущее окно: {f.Current.ToString("N0", Ru)}").FontSize(9).FontColor(Ink);
            if (f.FullPreviousWindows >= 1)
                t.Span($"   ·   предыдущее: {f.Prev1.ToString("N0", Ru)}").FontSize(9).FontColor(Muted);
        });
    }

    private void ComposeRecommendations(IContainer container)
    {
        container.Column(col =>
        {
            col.Spacing(4);
            col.Item().Text("Рекомендации").FontSize(16).Bold();

            foreach (var (priority, label, color) in PriorityGroups)
            {
                var items = model.Recommendations.Where(r => NormalizePriority(r.Priority) == priority).ToList();
                if (items.Count == 0) continue;

                col.Item().PaddingTop(6).Text($"{label} · {items.Count}").FontSize(11).SemiBold().FontColor(color);
                foreach (var r in items)
                {
                    col.Item().PaddingTop(3).Border(1).BorderColor(Line).Padding(8).Column(c =>
                    {
                        c.Spacing(2);
                        if (!string.IsNullOrWhiteSpace(r.Topic))
                            c.Item().Text(r.Topic).FontSize(8).FontColor(Muted);
                        c.Item().Text(r.Title).SemiBold().FontSize(10).FontColor(Ink);
                        if (!string.IsNullOrWhiteSpace(r.Body))
                            c.Item().Text(r.Body).FontSize(9).FontColor(Ink);
                        if (!string.IsNullOrWhiteSpace(r.ExpectedImpact))
                            c.Item().Text($"Ожидаемый эффект: {r.ExpectedImpact}").FontSize(8).FontColor(Muted);
                        foreach (var q in r.Evidence)
                            c.Item().BorderLeft(2).BorderColor(Line).PaddingLeft(6)
                                .Text($"«{q}»").FontSize(8).Italic().FontColor(Muted);
                    });
                }
            }
        });
    }

    private void ComposeExamples(IContainer container)
    {
        container.Column(col =>
        {
            col.Spacing(4);
            col.Item().Text("Примеры отзывов").FontSize(16).Bold();

            col.Item().PaddingTop(4).Text("Позитивные").SemiBold().FontColor(Green);
            if (model.Examples.Positive.Count == 0)
                col.Item().Text("Нет.").FontSize(9).FontColor(Muted);
            foreach (var ex in model.Examples.Positive)
                col.Item().Element(c => ComposeExampleCard(c, ex, Green));

            col.Item().PaddingTop(6).Text("Негативные").SemiBold().FontColor(Rose);
            if (model.Examples.Negative.Count == 0)
                col.Item().Text("Нет.").FontSize(9).FontColor(Muted);
            foreach (var ex in model.Examples.Negative)
                col.Item().Element(c => ComposeExampleCard(c, ex, Rose));
        });
    }

    private void ComposeExampleCard(IContainer container, ReportReviewExample ex, string accent)
    {
        container.PaddingTop(3).BorderLeft(3).BorderColor(accent).Background(Panel).Padding(8).Column(c =>
        {
            c.Spacing(2);
            c.Item().Text(t =>
            {
                t.Span(ex.SourceLabel).FontSize(8).SemiBold().FontColor(Ink);
                t.Span($"   {ex.Date.ToLocalTime():dd.MM.yyyy}").FontSize(8).FontColor(Muted);
                if (ex.Stars is { } st) t.Span($"   {st}★").FontSize(8).FontColor(Muted);
            });
            c.Item().Text(ex.Text).FontSize(9).FontColor(Ink);
        });
    }

    // priority 1=высокий … 3=низкий; вне диапазона → низкий (как на дашборде/RecommendationsBlock).
    private static short NormalizePriority(short p) => p is 1 or 2 ? p : (short)3;

    private static readonly (short Priority, string Label, string Color)[] PriorityGroups =
    [
        (1, "Высокий приоритет", Rose),
        (2, "Средний приоритет", "#d97706"),
        (3, "Низкий приоритет", Slate),
    ];

    private static string Pct(long part, long total) =>
        total <= 0 ? "—" : $"{Math.Round((double)part / total * 100).ToString("0", Ru)}%";

    private static string Signed(double v)
    {
        var rounded = Math.Round(v);
        return (rounded > 0 ? "+" : "") + rounded.ToString("0", Ru);
    }
}
