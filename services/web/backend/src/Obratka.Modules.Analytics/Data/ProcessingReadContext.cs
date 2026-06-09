using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Obratka.Modules.Analytics.Data.Entities;

namespace Obratka.Modules.Analytics.Data;

// Read-only DbContext к processing_db через PG-пользователя analytics_reader
// (ADR-011 §«MVP trade-off»). На уровне PG роль имеет SELECT-only права; на
// уровне кода — этот контекст явно бросает на SaveChanges, чтобы случайный
// вызов через DI всплыл сразу, а не упал тихо на стороне БД с access denied.
//
// Расширяем DbSet'ами по мере появления метрик. Сейчас покрывает только то,
// что нужно метрике 1 (Review + JOIN на ReviewLlmResult по sentiments
// + AnalysisJobReview для контекста job-а).
public sealed class ProcessingReadContext(DbContextOptions<ProcessingReadContext> options)
    : DbContext(options)
{
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<ReviewLlmResult> ReviewLlmResults => Set<ReviewLlmResult>();
    public DbSet<AnalysisJobReview> AnalysisJobReviews => Set<AnalysisJobReview>();
    public DbSet<AnalysisRecommendation> AnalysisRecommendations => Set<AnalysisRecommendation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Review>(e =>
        {
            e.ToTable("reviews", "public");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.CompanyId).HasColumnName("company_id");
            e.Property(r => r.BranchId).HasColumnName("branch_id");
            e.Property(r => r.Source).HasColumnName("source");
            e.Property(r => r.ReviewDate).HasColumnName("review_date");
            e.Property(r => r.CollectedAt).HasColumnName("collected_at");
            e.Property(r => r.Stars).HasColumnName("stars");
            e.Property(r => r.RawText).HasColumnName("raw_text");
        });

        modelBuilder.Entity<ReviewLlmResult>(e =>
        {
            e.ToTable("review_llm_results", "public");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.ReviewId).HasColumnName("review_id");
            e.Property(r => r.AnalysisJobId).HasColumnName("analysis_job_id");
            e.Property(r => r.OverallSentiment).HasColumnName("overall_sentiment");
            e.Property(r => r.OverallConfidence).HasColumnName("overall_confidence");
        });

        modelBuilder.Entity<AnalysisJobReview>(e =>
        {
            e.ToTable("analysis_job_reviews", "public");
            e.HasKey(r => new { r.AnalysisJobId, r.ReviewId });
            e.Property(r => r.AnalysisJobId).HasColumnName("analysis_job_id");
            e.Property(r => r.ReviewId).HasColumnName("review_id");
        });

        modelBuilder.Entity<AnalysisRecommendation>(e =>
        {
            e.ToTable("analysis_recommendations", "public");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.AnalysisJobId).HasColumnName("analysis_job_id");
            e.Property(r => r.Priority).HasColumnName("priority");
            e.Property(r => r.Topic).HasColumnName("topic");
            e.Property(r => r.Title).HasColumnName("title");
            e.Property(r => r.Body).HasColumnName("body");
            e.Property(r => r.ExpectedImpact).HasColumnName("expected_impact");
            // evidence — jsonb array of strings. В Npgsql 8+ нативный маппинг
            // List<string> ↔ jsonb требует EnableDynamicJson на DataSource —
            // это глобальный opt-in для всех типов, не хотим. Вместо этого
            // явная конверсия через System.Text.Json: при чтении парсим строку,
            // при записи сериализуем. Сериализация фактически не нужна
            // (контекст read-only), но HasConversion требует обе стороны.
            e.Property(r => r.Evidence)
                .HasColumnName("evidence")
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => string.IsNullOrEmpty(v)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(v, JsonOptions) ?? new List<string>(),
                    EvidenceComparer);
            e.Property(r => r.SortOrder).HasColumnName("sort_order");
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Компаратор для evidence (List<string> через value-converter). EF требует его для
    // корректного сравнения элементов коллекции (иначе CollectionWithoutComparer, EventId 10620).
    // Контекст read-only (change-tracking фактически не используется), но делаем корректный
    // deep-компаратор: поэлементное равенство + снапшот через копию.
    private static readonly ValueComparer<List<string>> EvidenceComparer = new(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s)),
        v => v == null ? new List<string>() : v.ToList());

    public override int SaveChanges()
        => throw new InvalidOperationException(
            "ProcessingReadContext is read-only (analytics_reader role has SELECT only).");

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        => throw new InvalidOperationException(
            "ProcessingReadContext is read-only (analytics_reader role has SELECT only).");
}
