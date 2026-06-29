namespace Obratka.Modules.Reports;

// Плоская модель PDF-отчёта. Заполняется в Web API (ReportDataAssembler) из метрик-сервисов
// Analytics — модуль Reports её только рендерит (QuestPDF), сам в БД не лезет.
// Все числа уже посчитаны с учётом фильтров дашборда.
public sealed record ReportDocumentModel(
    ReportMeta Meta,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<ReportRecommendation> Recommendations,
    ReportExamples Examples);

public sealed record ReportMeta(
    string CompanyName,
    DateTimeOffset GeneratedAt,
    string PeriodLabel,
    IReadOnlyList<string> SourceLabels,
    string StatusLabel,
    IReadOnlyList<ReportBranchLine> Branches,
    // Доп. строки про применённые фильтры (рейтинг/тональность), если они сужают выборку.
    IReadOnlyList<string> AppliedFilterLines);

public sealed record ReportBranchLine(string Address, string? Name, string? City);

// Одна секция отчёта: либо «По сети» (IsNetwork=true, итоги по всем выбранным филиалам),
// либо конкретный филиал.
public sealed record ReportSection(
    string Title,
    string? Subtitle,
    bool IsNetwork,
    ReportReviewCount ReviewCount,
    ReportAverageRating AverageRating,
    ReportSentiment Sentiment,
    ReportFreshPulse FreshPulse,
    ReportRecommendPercent RecommendPercent,
    IReadOnlyList<ReportTopic> Topics,
    long TopicsTotalReviews,
    ReportFlow Flow);

public sealed record ReportReviewCount(long Total, IReadOnlyList<ReportSourceCount> BySource);
public sealed record ReportSourceCount(string SourceLabel, long Count);

public sealed record ReportAverageRating(double? Total, IReadOnlyList<ReportSourceRating> BySource);
public sealed record ReportSourceRating(string SourceLabel, double? Average, int Count);

public sealed record ReportSentiment(long Positive, long Neutral, long Negative, long Total);

// Index ∈ [-100,+100] или null (нет размеченных отзывов за окно).
public sealed record ReportFreshPulse(double? Index, DateTimeOffset From, DateTimeOffset To, long TotalNonEmpty);

public sealed record ReportRecommendPercent(long Positive, long Total);

public sealed record ReportTopic(string Topic, long ReviewCount, long PositiveMentions, long NegativeMentions);

public sealed record ReportFlow(
    string WindowLabel,
    long Current,
    long Prev1,
    long Prev2,
    long Prev3,
    int FullPreviousWindows);

// Priority: 1=высокий, 2=средний, 3=низкий (как на дашборде).
public sealed record ReportRecommendation(
    short Priority,
    string Topic,
    string Title,
    string Body,
    string? ExpectedImpact,
    IReadOnlyList<string> Evidence);

public sealed record ReportExamples(
    IReadOnlyList<ReportReviewExample> Positive,
    IReadOnlyList<ReportReviewExample> Negative);

public sealed record ReportReviewExample(string SourceLabel, DateTimeOffset Date, short? Stars, string Text);
