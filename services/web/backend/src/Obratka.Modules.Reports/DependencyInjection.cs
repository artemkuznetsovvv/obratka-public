using Microsoft.Extensions.DependencyInjection;

namespace Obratka.Modules.Reports;

public static class DependencyInjection
{
    public static IServiceCollection AddReportsModule(this IServiceCollection services)
    {
        services.AddScoped<IReportsModule, ReportsModule>();
        return services;
    }
}
