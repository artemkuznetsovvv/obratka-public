using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Metrics.AverageRating;
using Obratka.Modules.Analytics.Metrics.FreshPulse;
using Obratka.Modules.Analytics.Metrics.RecentReviews;
using Obratka.Modules.Analytics.Metrics.RecommendPercent;
using Obratka.Modules.Analytics.Metrics.ReviewCount;
using Obratka.Modules.Analytics.Metrics.SentimentDistribution;
using Obratka.Modules.Analytics.Metrics.TopTopics;
using Obratka.Modules.Analytics.Monitoring;
using Obratka.Modules.Analytics.Recommendations;

namespace Obratka.Modules.Analytics;

public static class DependencyInjection
{
    // Имя ключа для connection string к processing_db через analytics_reader.
    // Локально кладём в appsettings.Development.json; на стенде — env
    // ConnectionStrings__ProcessingReadDb.
    public const string ProcessingReadConnectionName = "ProcessingReadDb";

    public static IServiceCollection AddAnalyticsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IAnalyticsModule, AnalyticsModule>();

        // Conditional регистрация: если connection string не задан — DbContext
        // не регистрируем, а endpoint'ы метрик вернут 503 (см. контроллер). Это
        // позволяет поднимать Web API локально без Analytics-зависимости
        // (например, для воронки запуска анализа).
        var cs = configuration.GetConnectionString(ProcessingReadConnectionName);
        if (!string.IsNullOrWhiteSpace(cs))
        {
            services.AddDbContext<ProcessingReadContext>(options =>
                options
                    .UseNpgsql(cs)
                    // Метрики аналитики используют идиом GroupBy(_ => 1).Select(агрегаты).First():
                    // константный ключ → ровно одна группа, результат детерминирован, OrderBy не нужен.
                    // EF-предупреждение FirstWithoutOrderByAndFilterWarning для этого идиома ложно-
                    // положительное → глушим его ТОЛЬКО на read-only контексте аналитики (в основном
                    // webapi_db оно остаётся активным и ловит реальные недетерминированные First).
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.FirstWithoutOrderByAndFilterWarning)));
            services.AddScoped<IReviewCountMetricService, ReviewCountMetricService>();
            services.AddScoped<IAverageRatingMetricService, AverageRatingMetricService>();
            services.AddScoped<ISentimentDistributionMetricService, SentimentDistributionMetricService>();
            services.AddScoped<ISentimentReviewsService, SentimentReviewsService>();
            services.AddScoped<IFreshPulseMetricService, FreshPulseMetricService>();
            services.AddScoped<ITopTopicsMetricService, TopTopicsMetricService>();
            services.AddScoped<IRecommendPercentMetricService, RecommendPercentMetricService>();
            services.AddScoped<IRecentReviewsMetricService, RecentReviewsMetricService>();
            services.AddScoped<IRecommendationsService, RecommendationsService>();
            services.AddScoped<IMonitoringStatsService, MonitoringStatsService>();
        }

        return services;
    }
}
