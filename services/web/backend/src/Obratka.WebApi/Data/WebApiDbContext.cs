using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Geo;
using Obratka.WebApi.Monitoring;
using Obratka.WebApi.Notifications;
using Obratka.WebApi.Support;

namespace Obratka.WebApi.Data;

public class WebApiDbContext(DbContextOptions<WebApiDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyBranch> CompanyBranches => Set<CompanyBranch>();
    public DbSet<LogicalBranch> LogicalBranches => Set<LogicalBranch>();
    public DbSet<SearchCacheEntry> SearchCache => Set<SearchCacheEntry>();
    public DbSet<CityReference> Cities => Set<CityReference>();
    public DbSet<MonitoringConfig> MonitoringConfigs => Set<MonitoringConfig>();
    public DbSet<MonitoringCycle> MonitoringCycles => Set<MonitoringCycle>();
    public DbSet<UserRequest> UserRequests => Set<UserRequest>();
    public DbSet<TelegramLinkToken> TelegramLinkTokens => Set<TelegramLinkToken>();
    public DbSet<AnalysisNotification> AnalysisNotifications => Set<AnalysisNotification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Один Telegram-чат может принадлежать только одному аккаунту (защита от утечки между
        // аккаунтами). Частичный уникальный индекс — только для непустых значений.
        builder.Entity<ApplicationUser>()
            .HasIndex(u => u.TelegramChatId)
            .IsUnique()
            .HasFilter("\"TelegramChatId\" IS NOT NULL");

        builder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.Token).HasMaxLength(500).IsRequired();
            b.HasIndex(x => x.Token).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Company>(b =>
        {
            b.ToTable("companies");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Category).HasMaxLength(100);
            b.Property(x => x.Subcategory).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.Property(x => x.Cities).HasColumnType("text[]");
            b.Property(x => x.DraftSources).HasColumnType("text[]");
            b.HasIndex(x => x.OwnerUserId);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Branches)
                .WithOne()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CompanyBranch>(b =>
        {
            b.ToTable("company_branches");
            b.HasKey(x => x.Id);
            b.Property(x => x.Source).HasMaxLength(32).IsRequired();
            b.Property(x => x.ExternalId).HasMaxLength(256).IsRequired();
            b.Property(x => x.ExternalUrl).HasMaxLength(1024);
            b.Property(x => x.Name).HasMaxLength(500).IsRequired();
            b.Property(x => x.Address).HasMaxLength(1024);
            b.Property(x => x.City).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.CompanyId);
            b.HasIndex(x => x.LogicalBranchId);
            // Partial unique index: only enforce when ExternalId is non-empty.
            // Parser plugins sometimes return null/empty externalId — those rows must coexist.
            b.HasIndex(x => new { x.CompanyId, x.Source, x.ExternalId })
                .IsUnique()
                .HasFilter("\"ExternalId\" <> ''");
        });

        builder.Entity<LogicalBranch>(b =>
        {
            b.ToTable("logical_branches");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(500).IsRequired();
            b.Property(x => x.Address).HasMaxLength(1024).IsRequired();
            b.Property(x => x.City).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.CompanyId);
            b.HasOne<Company>()
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            // Provider cards inside a logical branch. SetNull on delete so wiping a logical
            // branch returns its cards to «unmatched» rather than dropping them entirely.
            b.HasMany(x => x.Cards)
                .WithOne()
                .HasForeignKey(c => c.LogicalBranchId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CityReference>(b =>
        {
            b.ToTable("city_reference");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.NameNormalized).HasMaxLength(200).IsRequired();
            b.Property(x => x.Region).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.NameNormalized);
            b.HasIndex(x => new { x.Name, x.Region }).IsUnique();
        });

        builder.Entity<SearchCacheEntry>(b =>
        {
            b.ToTable("search_cache");
            b.HasKey(x => x.Id);
            b.Property(x => x.QueryNormalized).HasMaxLength(500).IsRequired();
            b.Property(x => x.CityNormalized).HasMaxLength(200).IsRequired();
            b.Property(x => x.Source).HasMaxLength(32).IsRequired();
            b.Property(x => x.Results)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<SearchCacheItem>>(v, JsonOptions) ?? new(),
                    new ValueComparer<List<SearchCacheItem>>(
                        (a, b) => ReferenceEquals(a, b),
                        v => v == null ? 0 : v.Count,
                        v => v));
            b.HasIndex(x => new { x.QueryNormalized, x.CityNormalized, x.Source }).IsUnique();
            b.HasIndex(x => x.ExpiresAt);
        });

        builder.Entity<MonitoringConfig>(b =>
        {
            b.ToTable("monitoring_configs");
            b.HasKey(x => x.Id);
            // Sources — slug-и (text[]), как Company.Cities/DraftSources. BranchIds — uuid[].
            b.Property(x => x.Sources).HasColumnType("text[]");
            b.Property(x => x.BranchIds).HasColumnType("uuid[]");
            b.Property(x => x.CronSchedule).HasMaxLength(100).IsRequired();
            // Enum'ы — строками (читабельно в БД + стабильно при добавлении значений).
            b.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.LastRunStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.NotificationsEnabled).HasDefaultValue(true);
            b.HasIndex(x => x.CompanyId);
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => x.SeedJobId);
            b.HasOne<Company>()
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Cycles)
                .WithOne()
                .HasForeignKey(c => c.MonitoringId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MonitoringCycle>(b =>
        {
            b.ToTable("monitoring_cycles");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.SummarySnapshot).HasMaxLength(4000);
            // Снапшот рекомендаций PG на момент цикла — jsonb (как SearchCacheEntry.Results).
            b.Property(x => x.RecommendationsSnapshot)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<RecommendationSnapshotItem>>(v, JsonOptions) ?? new(),
                    new ValueComparer<List<RecommendationSnapshotItem>>(
                        (a, b) => ReferenceEquals(a, b),
                        v => v == null ? 0 : v.Count,
                        v => v));
            b.HasIndex(x => x.MonitoringId);
            b.HasIndex(x => new { x.MonitoringId, x.CycleNumber }).IsUnique();
        });

        builder.Entity<TelegramLinkToken>(b =>
        {
            b.ToTable("telegram_link_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.Token).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.Token).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => x.ExpiresAt);
            b.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AnalysisNotification>(b =>
        {
            b.ToTable("analysis_notifications");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.JobId).IsUnique();
            // Частичный индекс под выборку «ещё не уведомлённых» в reconcile-джобе.
            b.HasIndex(x => x.NotifiedAt).HasFilter("\"NotifiedAt\" IS NULL");
        });

        builder.Entity<UserRequest>(b =>
        {
            b.ToTable("user_requests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.Property(x => x.Message).HasMaxLength(2000);
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.CreatedAt);
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
