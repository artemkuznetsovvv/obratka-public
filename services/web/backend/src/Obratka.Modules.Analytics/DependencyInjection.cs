using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Obratka.Modules.Analytics.Data;
using Obratka.Modules.Analytics.Metrics.AverageRating;
using Obratka.Modules.Analytics.Metrics.ReviewCount;
using Obratka.Modules.Analytics.Metrics.SentimentDistribution;

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
                options.UseNpgsql(cs));
            services.AddScoped<IReviewCountMetricService, ReviewCountMetricService>();
            services.AddScoped<IAverageRatingMetricService, AverageRatingMetricService>();
            services.AddScoped<ISentimentDistributionMetricService, SentimentDistributionMetricService>();
            services.AddScoped<ISentimentReviewsService, SentimentReviewsService>();
        }

        return services;
    }
}
