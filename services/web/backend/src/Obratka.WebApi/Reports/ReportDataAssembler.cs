using Microsoft.EntityFrameworkCore;
using Obratka.Modules.Analytics.Metrics.AverageRating;
using Obratka.Modules.Analytics.Metrics.FreshPulse;
using Obratka.Modules.Analytics.Metrics.RecentReviews;
using Obratka.Modules.Analytics.Metrics.RecommendPercent;
using Obratka.Modules.Analytics.Metrics.ReviewCount;
using Obratka.Modules.Analytics.Metrics.SentimentDistribution;
using Obratka.Modules.Analytics.Metrics.TopTopics;
using Obratka.Modules.Analytics.Recommendations;
using Obratka.Modules.Reports;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ProcessingGateway.Contracts;

namespace Obratka.WebApi.Reports;

// Собирает ReportDocumentModel из тех же метрик-сервисов Analytics, что и дашборд —
// никакого собственного compute. Резолвится через ActivatorUtilities (Program.cs):
// метрик-сервисы зарегистрированы ТОЛЬКО при заданном ConnectionStrings:ProcessingReadDb,
// иначе придут null → IsAvailable=false → контроллер вернёт 503 (как в DashboardMetricsController).
public sealed class ReportDataAssembler(
    WebApiDbContext db,
    IReviewCountMetricService? reviewCount = null,
    IAverageRatingMetricService? averageRating = null,
    ISentimentDistributionMetricService? sentimentDistribution = null,
    IFreshPulseMetricService? freshPulse = null,
    ITopTopicsMetricService? topTopics = null,
    IRecommendPercentMetricService? recommendPercent = null,
    IRecentReviewsMetricService? recentReviews = null,
    ISentimentReviewsService? sentimentReviews = null,
    IRecommendationsService? recommendations = null)
{
    private static readonly string[] FixedSources = ["2gis", "yandex", "google"];
    private const int ExampleLimit = 5;
    private const int ExampleTextMax = 600;

    // Все Analytics-сервисы регистрируются одним блоком — проверки одного достаточно.
    public bool IsAvailable => reviewCount is not null;

    public async Task<ReportDocumentModel> BuildAsync(
        AnalysisJobDto job,
        string companyName,
        IReadOnlyList<Guid> selectedBranchIds,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<string>? sources,
        IReadOnlyCollection<string>? sentiments,
        IReadOnlyCollection<short>? stars,
        CancellationToken ct)
    {
        // Инфо филиалов (имя/адрес/город) из webapi_db. Удалённый после запуска филиал
        // в LogicalBranches отсутствует — он не получит отдельную страницу, но в network-scope
        // (метрики по branchIds) всё равно учитывается.
        var branchInfos = await db.LogicalBranches.AsNoTracking()
            .Where(lb => lb.CompanyId == job.CompanyId && selectedBranchIds.Contains(lb.Id))
            .Select(lb => new BranchInfo(lb.Id, lb.Name, lb.Address, lb.City))
            .ToListAsync(ct);
        var byId = branchInfos.ToDictionary(b => b.Id);
        var orderedBranches = selectedBranchIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        // ---- Sections ----
        var sections = new List<ReportSection>();
        if (selectedBranchIds.Count <= 1)
        {
            var info = orderedBranches.FirstOrDefault();
            sections.Add(await BuildSectionAsync(
                job, selectedBranchIds, BranchTitle(info), BranchSubtitle(info),
                isNetwork: false, from, to, sources, sentiments, stars, ct));
        }
        else
        {
            sections.Add(await BuildSectionAsync(
                job, selectedBranchIds, "По сети",
                $"Итоги по {selectedBranchIds.Count} {Plural(selectedBranchIds.Count, "филиалу", "филиалам", "филиалам")}",
                isNetwork: true, from, to, sources, sentiments, stars, ct));

            foreach (var info in orderedBranches)
                sections.Add(await BuildSectionAsync(
                    job, [info.Id], BranchTitle(info), BranchSubtitle(info),
                    isNetwork: false, from, to, sources, sentiments, stars, ct));
        }

        // ---- Recommendations (job-level) ----
        var recs = recommendations is null
            ? Array.Empty<ReportRecommendation>()
            : (await recommendations.ListByJobAsync(job.Id, ct))
                .Select(r => new ReportRecommendation(
                    r.Priority, r.Topic, r.Title, r.Body, r.ExpectedImpact, r.Evidence))
                .ToArray();

        // ---- Top examples (scope = все выбранные филиалы) ----
        var examples = await BuildExamplesAsync(job, selectedBranchIds, from, to, sources, stars, ct);

        // ---- Meta ----
        var scopedSources = sources is { Count: > 0 }
            ? sources.ToList()
            : job.CollectionProgress.Keys.ToList();

        var meta = new ReportMeta(
            CompanyName: companyName,
            GeneratedAt: DateTimeOffset.UtcNow,
            PeriodLabel: PeriodLabel(from, to),
            SourceLabels: scopedSources.Select(SourceLabel).ToList(),
            StatusLabel: StatusLabel(job.Status),
            Branches: orderedBranches.Select(b => new ReportBranchLine(b.Address, b.Name, b.City)).ToList(),
            AppliedFilterLines: BuildAppliedFilterLines(sentiments, stars));

        return new ReportDocumentModel(meta, sections, recs, examples);
    }

    private async Task<ReportSection> BuildSectionAsync(
        AnalysisJobDto job,
        IReadOnlyCollection<Guid> scope,
        string title,
        string? subtitle,
        bool isNetwork,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<string>? sources,
        IReadOnlyCollection<string>? sentiments,
        IReadOnlyCollection<short>? stars,
        CancellationToken ct)
    {
        // Правила фильтров per-метрика — строго как в DashboardMetricsController.
        var rc = await reviewCount!.ComputeAsync(
            new ReviewCountMetricQuery(job.Id, scope, from, to, sentiments, stars), ct);
        var ar = await averageRating!.ComputeAsync(
            new AverageRatingMetricQuery(job.Id, scope, from, to, sentiments, stars), ct);
        var sd = await sentimentDistribution!.ComputeAsync(
            new SentimentDistributionQuery(job.Id, scope, from, to, sources, stars), ct);
        var fp = await freshPulse!.ComputeAsync(
            new FreshPulseMetricQuery(job.Id, scope, sources, stars), ct);
        var rp = await recommendPercent!.ComputeAsync(
            new RecommendPercentMetricQuery(job.Id, scope, from, to, sources, stars), ct);
        var tt = await topTopics!.ComputeAsync(
            new TopTopicsMetricQuery(job.Id, scope, from, to, sources, stars), ct);
        var rr = await recentReviews!.ComputeAsync(
            new RecentReviewsMetricQuery(job.Id, scope, RecentReviewsWindow.Days7, sources, stars), ct);

        var bySourceCount = FixedSources
            .Select(src => new ReportSourceCount(
                SourceLabel(src),
                rc.BySource.TryGetValue(src, out var c) ? c.Current : 0))
            .ToList();

        var bySourceRating = FixedSources
            .Select(src =>
            {
                ar.BySource.TryGetValue(src, out var sa);
                return new ReportSourceRating(SourceLabel(src), sa?.Average, sa?.Count ?? 0);
            })
            .ToList();

        return new ReportSection(
            Title: title,
            Subtitle: subtitle,
            IsNetwork: isNetwork,
            ReviewCount: new ReportReviewCount(rc.TotalCurrent, bySourceCount),
            AverageRating: new ReportAverageRating(ar.TotalAverage, bySourceRating),
            Sentiment: new ReportSentiment(sd.Positive, sd.Neutral, sd.Negative, sd.TotalNonEmpty),
            FreshPulse: new ReportFreshPulse(
                fp.Current.Index, fp.Current.FromInclusive, fp.Current.ToExclusive, fp.Current.TotalNonEmpty),
            RecommendPercent: new ReportRecommendPercent(rp.Current.Positive, rp.Current.TotalNonEmpty),
            Topics: tt.Topics
                .Select(t => new ReportTopic(t.Topic, t.ReviewCount, t.PositiveMentions, t.NegativeMentions))
                .ToList(),
            TopicsTotalReviews: tt.TotalReviewsInPeriod,
            Flow: new ReportFlow("7 дней", rr.CurrentCount, rr.Prev1Count, rr.Prev2Count, rr.Prev3Count, rr.FullPreviousWindows));
    }

    private async Task<ReportExamples> BuildExamplesAsync(
        AnalysisJobDto job,
        IReadOnlyList<Guid> branchIds,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IReadOnlyCollection<string>? sources,
        IReadOnlyCollection<short>? stars,
        CancellationToken ct)
    {
        if (sentimentReviews is null)
            return new ReportExamples([], []);

        var pos = await sentimentReviews.ListAsync(
            new SentimentReviewsQuery(job.Id, branchIds, "позитивный", from, to, sources, stars, ExampleLimit, 0), ct);
        var neg = await sentimentReviews.ListAsync(
            new SentimentReviewsQuery(job.Id, branchIds, "негативный", from, to, sources, stars, ExampleLimit, 0), ct);

        return new ReportExamples(
            pos.Items.Select(ToExample).ToList(),
            neg.Items.Select(ToExample).ToList());
    }

    private static ReportReviewExample ToExample(SentimentReviewItem i) =>
        new(SourceLabel(i.Source), i.ReviewDate, i.Stars, Truncate(i.Text, ExampleTextMax));

    private static List<string> BuildAppliedFilterLines(
        IReadOnlyCollection<string>? sentiments, IReadOnlyCollection<short>? stars)
    {
        var lines = new List<string>();
        if (stars is { Count: > 0 } && stars.Count < 5)
            lines.Add("Рейтинг (звёзды): " + string.Join(", ", stars.OrderBy(x => x)));
        if (sentiments is { Count: > 0 } && sentiments.Count < 3)
            lines.Add("Тональность: " + string.Join(", ", sentiments));
        return lines;
    }

    private static string BranchTitle(BranchInfo? info)
    {
        if (info is null) return "Филиал";
        return string.IsNullOrWhiteSpace(info.Address) ? info.Name : info.Address;
    }

    private static string? BranchSubtitle(BranchInfo? info)
    {
        if (info is null) return null;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Address) && !string.IsNullOrWhiteSpace(info.Name)) parts.Add(info.Name);
        if (!string.IsNullOrWhiteSpace(info.City)) parts.Add(info.City);
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string SourceLabel(string slug) => slug switch
    {
        "2gis" => "2ГИС",
        "yandex" => "Яндекс.Карты",
        "google" => "Google Maps",
        "otzovik" => "Отзовик",
        _ => slug,
    };

    private static string StatusLabel(string status) => status switch
    {
        "completed" => "Завершён",
        "partial" => "Частично",
        "failed" => "Ошибка",
        "collecting" => "Идёт сбор",
        "sent_to_llm" => "Обработка LLM",
        "computing_aggregates" => "Расчёт агрегатов",
        "pending" => "В очереди",
        _ => status,
    };

    private static string PeriodLabel(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null && to is null) return "С самого начала";
        var f = from?.ToString("dd.MM.yyyy") ?? "…";
        var t = to?.ToString("dd.MM.yyyy") ?? "…";
        return $"{f} — {t}";
    }

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    // Русская плюрализация для счётчиков.
    private static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return one;
        if (mod10 is >= 2 and <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
        return many;
    }

    private sealed record BranchInfo(Guid Id, string Name, string Address, string City);
}
