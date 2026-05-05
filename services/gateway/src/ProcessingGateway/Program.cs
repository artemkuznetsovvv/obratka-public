using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Parser;
using ProcessingGateway.Infrastructure.Storage;
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

// HTTP-доступ для CorrelationId-проброса (DelegatingHandler читает HttpContext).
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();

// Parser HTTP-клиент. ADR-001 §4: per-source POST /api/collection-tasks + GET polling.
// AddStandardResilienceHandler даёт retry с экспоненциальным backoff, circuit breaker и timeout —
// дефолтные пресеты Microsoft.Extensions.Http.Resilience.
var parserBaseUrl = builder.Configuration["Parser:BaseUrl"]
    ?? throw new InvalidOperationException("Parser:BaseUrl is not configured");
builder.Services
    .AddHttpClient<IParserClient, ParserHttpClient>(client =>
    {
        client.BaseAddress = new Uri(parserBaseUrl.TrimEnd('/') + "/");
    })
    .AddHttpMessageHandler<CorrelationIdHandler>()
    .AddStandardResilienceHandler();

// AmazonS3 → MinIO. ForcePathStyle=true ОБЯЗАТЕЛЕН (см. Parser-Service/deploy-vps.md §15.4).
// На VPS PG живёт в `parser-service_internal` и ходит во внутренний http://minio:9000
// с root-credentials Parser-а. Внешний vhost `s3.193.233.217.223.sslip.io` — для LLM,
// не для PG.
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["S3:Endpoint"]
        ?? throw new InvalidOperationException("S3:Endpoint is not configured");
    var accessKey = cfg["S3:AccessKey"]
        ?? throw new InvalidOperationException("S3:AccessKey is not configured");
    var secretKey = cfg["S3:SecretKey"]
        ?? throw new InvalidOperationException("S3:SecretKey is not configured");

    return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = cfg["S3:Region"] ?? "us-east-1"
    });
});
builder.Services.AddSingleton<IJobBlobStorage, S3JobBlobStorage>();

// Healthchecks: liveness без проб; readiness — Postgres. S3- и Parser-пробы
// поедут в QA-эндпоинт `/api/qa/health/dependencies` на Этапе 8 (CLAUDE.md / план).
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
