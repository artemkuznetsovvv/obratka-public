using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Telemetry;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Bootstrap Serilog (ADR-008): Console + Seq, enricher Service="ProcessingGateway",
// CorrelationId через LogContext. Seq URL приходит из конфига; пусто → только консоль.
builder.Host.UseSerilog((context, services, config) =>
{
    var seqUrl = context.Configuration["Seq:ServerUrl"];
    var seqApiKey = context.Configuration["Seq:ApiKey"];

    config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Service", "ProcessingGateway")
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}");

    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        config.WriteTo.Seq(seqUrl, apiKey: string.IsNullOrWhiteSpace(seqApiKey) ? null : seqApiKey);
    }
});

// EF Core / Postgres. snake_case naming convention, чтобы колонки/таблицы соответствовали ADR-002.
var connectionString = builder.Configuration.GetConnectionString("ProcessingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ProcessingDb is not configured");

builder.Services.AddDbContext<ProcessingDbContext>(options =>
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention());

// Dapper-фабрика для bulk-INSERT путей (Этапы 3, 6).
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

// Bulk-INSERT отзывов из raw/{source}.json. Stateless через factory — singleton.
builder.Services.AddSingleton<RawReviewBulkInserter>();

// Healthchecks: liveness без проб; readiness теперь имеет реальную пробу — Postgres.
builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionString: connectionString,
        name: "processing-db",
        tags: new[] { "ready" });

var app = builder.Build();

// Применяем миграции на старте (как в Parser-е). На Этапе 2 миграция одна — Initial.
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ProcessingDbContext>();
    await dbContext.Database.MigrateAsync();
    Log.Information("EF migrations applied");
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

// Liveness — процесс жив, слушает HTTP. Без проб.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

// Readiness — все пробы с тегом "ready" зелёные.
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

Log.Information("ProcessingGateway started, environment={Environment}", app.Environment.EnvironmentName);

app.Run();

// Для WebApplicationFactory<Program> в интеграционных тестах.
public partial class Program;
