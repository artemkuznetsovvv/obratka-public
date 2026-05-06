using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ProcessingGateway.Domain;

namespace ProcessingGateway.Infrastructure.Database;

/// Владелец `processing_db`. Схема — ADR-002/004, snake_case через
/// `UseSnakeCaseNamingConvention()` в Program.cs. Outbox/Inbox-таблицы MassTransit здесь же.
public class ProcessingDbContext : DbContext
{
    public ProcessingDbContext(DbContextOptions<ProcessingDbContext> options) : base(options) { }

    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<ReviewLlmResult> ReviewLlmResults => Set<ReviewLlmResult>();
    public DbSet<AnalysisJobReview> AnalysisJobReviews => Set<AnalysisJobReview>();
    public DbSet<AnalysisRecommendation> AnalysisRecommendations => Set<AnalysisRecommendation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // MassTransit Outbox/Inbox (решение Этапа 0 №3). Таблицы создаются вместе с миграцией.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        ConfigureAnalysisJob(modelBuilder);
        ConfigureReview(modelBuilder);
        ConfigureReviewLlmResult(modelBuilder);
        ConfigureAnalysisJobReview(modelBuilder);
        ConfigureAnalysisRecommendation(modelBuilder);
    }

    private static void ConfigureAnalysisJobReview(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisJobReview>(b =>
        {
            b.ToTable("analysis_job_reviews");
            b.HasKey(x => new { x.AnalysisJobId, x.ReviewId });

            b.HasOne(x => x.Review)
                .WithMany()
                .HasForeignKey(x => x.ReviewId)
                .OnDelete(DeleteBehavior.Cascade);

            // Обратный индекс: «в каких job-ах участвовал этот review» (редко, но дёшево).
            b.HasIndex(x => x.ReviewId)
                .HasDatabaseName("ix_analysis_job_reviews_review");
        });
    }

    private static void ConfigureAnalysisJob(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisJob>(b =>
        {
            b.ToTable("analysis_jobs");
            b.HasKey(x => x.Id);

            // Внешний UUID — клиент (Web API / QA-эндпоинт) генерирует и протекает дальше.
            b.Property(x => x.Id).ValueGeneratedNever();

            b.Property(x => x.Status)
                .HasMaxLength(40)
                .HasConversion(
                    v => v.ToWire(),
                    v => AnalysisJobStatusExtensions.FromWire(v));

            b.Property(x => x.ReviewCount).HasDefaultValue(0);

            b.Property(x => x.CollectionProgress)
                .HasColumnType("jsonb")
                .HasConversion(JsonStringConverter<Dictionary<string, CollectionProgressEntry>>())
                .Metadata.SetValueComparer(JsonStringComparer<Dictionary<string, CollectionProgressEntry>>());

            b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");
        });
    }

    private static void ConfigureReview(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Review>(b =>
        {
            b.ToTable("reviews");
            b.HasKey(x => x.Id);

            // bigint identity (отклонение от ADR-002 — см. Review.cs).
            b.Property(x => x.Id).UseIdentityByDefaultColumn();

            b.Property(x => x.Source).HasMaxLength(50);
            b.Property(x => x.ExternalId).HasMaxLength(500);
            b.Property(x => x.CompositeKey).HasMaxLength(1000);
            b.Property(x => x.TextLanguage).HasMaxLength(10);
            b.Property(x => x.CollectedAt).HasDefaultValueSql("NOW()");

            // ADR-002: дедуп.
            b.HasIndex(x => x.CompositeKey)
                .IsUnique()
                .HasDatabaseName("ix_reviews_composite_key");

            // ADR-002: уникальность по (source, branch_id, external_id) WHERE external_id IS NOT NULL.
            b.HasIndex(x => new { x.Source, x.BranchId, x.ExternalId })
                .IsUnique()
                .HasFilter("external_id IS NOT NULL")
                .HasDatabaseName("ix_reviews_source_branch_external");

            b.HasIndex(x => new { x.CompanyId, x.ReviewDate })
                .HasDatabaseName("ix_reviews_company_review_date")
                .IsDescending(false, true);
        });
    }

    private static void ConfigureReviewLlmResult(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewLlmResult>(b =>
        {
            b.ToTable("review_llm_results");
            b.HasKey(x => x.Id);

            b.Property(x => x.Id).UseIdentityByDefaultColumn();
            b.Property(x => x.OverallSentiment).HasMaxLength(20);
            b.Property(x => x.ProcessedAt).HasDefaultValueSql("NOW()");

            b.Property(x => x.Aspects)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(JsonStringConverter<List<ReviewAspect>>())
                .Metadata.SetValueComparer(JsonStringComparer<List<ReviewAspect>>());

            b.HasOne(x => x.Review)
                .WithMany()
                .HasForeignKey(x => x.ReviewId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => x.AnalysisJobId)
                .HasDatabaseName("ix_review_llm_results_analysis_job");

            b.HasIndex(x => new { x.ReviewId, x.AnalysisJobId })
                .IsUnique()
                .HasDatabaseName("ix_review_llm_results_review_job_unique");

            // GIN-индекс по `aspects` для фильтра «отзывы с темой X».
            // EF не умеет нативно описать GIN — добавляется в миграции вручную.
        });
    }

    private static void ConfigureAnalysisRecommendation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisRecommendation>(b =>
        {
            b.ToTable("analysis_recommendations");
            b.HasKey(x => x.Id);

            b.Property(x => x.Id).UseIdentityByDefaultColumn();
            b.Property(x => x.Topic).HasMaxLength(200);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("NOW()");

            b.Property(x => x.Evidence)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(JsonStringConverter<List<string>>())
                .Metadata.SetValueComparer(JsonStringComparer<List<string>>());

            b.HasOne(x => x.AnalysisJob)
                .WithMany()
                .HasForeignKey(x => x.AnalysisJobId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.AnalysisJobId, x.Priority, x.SortOrder })
                .HasDatabaseName("ix_analysis_recommendations_job_priority");
        });
    }

    // --- JSONB ↔ POCO через System.Text.Json. snake_case в JSON, чтобы
    // collection_progress читался привычно, а не TaskId/Status в JSONB. ---

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null, // ключи словаря (slug-и источников) уже в нужном виде
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static ValueConverter<T, string> JsonStringConverter<T>() where T : class => new(
        v => JsonSerializer.Serialize(v, JsonOptions),
        v => JsonSerializer.Deserialize<T>(v, JsonOptions)!);

    private static ValueComparer<T> JsonStringComparer<T>() where T : class => new(
        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);
}
