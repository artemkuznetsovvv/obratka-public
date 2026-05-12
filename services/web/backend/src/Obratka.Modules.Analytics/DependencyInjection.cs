using Microsoft.Extensions.DependencyInjection;

namespace Obratka.Modules.Analytics;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services)
    {
        services.AddScoped<IAnalyticsModule, AnalyticsModule>();
        return services;
    }
}
