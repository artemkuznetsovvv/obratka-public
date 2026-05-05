using Amazon.S3;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Application.Pipeline;
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

// Связь job ↔ review. Вызывается после bulk-INSERT-а отзывов (ParserPoller).
builder.Services.AddSingleton<JobReviewLinker>();

// Pipeline-блоки. Scoped — берут DbContext.
builder.Services.AddScoped<LlmDispatcher>();
builder.Services.AddScoped<AnalysisOrchestrator>();
builder.Services.AddHostedService<ParserPoller>();

// Контроллеры (QA-эндпоинты).
builder.Services.AddControllers();

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

// MassTransit + RabbitMQ + EF Outbox (решение №3 Этапа 0). Outbox-таблицы уже в БД из Initial-миграции.
// `UseBusOutbox()` гарантирует: publish происходит ПОСЛЕ commit DbContext.SaveChanges → атомарность.
//
// Host передаётся БЕЗ порта (`MassTransit.RabbitMqHostAddress` парсит ":" как часть URI и валится).
// Если задан `RabbitMq:Port` — используем перегрузку с явным портом, иначе AMQP default 5672.
var rabbitHost = builder.Configuration["RabbitMq:Host"]
    ?? throw new InvalidOperationException("RabbitMq:Host is not configured");
var rabbitPort = ushort.TryParse(builder.Configuration["RabbitMq:Port"], out var p) ? p : (ushort)5672;
var rabbitUser = builder.Configuration["RabbitMq:User"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Pass"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ProcessingGateway.Application.Consumers.StartAnalysisCommandConsumer>();

    x.AddEntityFrameworkOutbox<ProcessingDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, rabbitPort, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });

        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromSeconds(2),
            maxInterval: TimeSpan.FromSeconds(30),
            intervalDelta: TimeSpan.FromSeconds(5)));

        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30)));

        cfg.ConfigureEndpoints(context);
    });
});

// Healthchecks: минимальный self-check — процесс жив, DI собран. Дотошные пробы
// (Postgres / S3 / RabbitMQ / Parser) переедут в `/api/qa/health/dependencies` на Этапе 8.
//
// Раньше здесь был AddNpgSql(...) на readiness, но он триггерит DI graph во время
// MapHealthChecks → конфликтует с MassTransit EF Outbox-регистрацией DbContext.
// В .NET 9 без проб MapHealthChecks возвращает 503 (изменилось поведение vs .NET 8) —
// поэтому добавляем dummy "self".
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(),
        tags: new[] { "ready", "live" });

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

// Health endpoints. Оба без проб (`Predicate = _ => false`) — гарантированно 200,
// если процесс жив. Реальная диагностика зависимостей (Postgres / RabbitMQ / S3 /
// Parser) переедет в `/api/qa/health/dependencies` на Этапе 8.
//
// Почему не подключаем зависимостные пробы здесь: в .NET 9 MapHealthChecks при
// построении endpoint-a проходится по всем registered HealthCheckRegistration через
// DI graph. MassTransit регистрирует свой bus-health-check, у которого constructor
// зависит от scoped IBusInstance — и это валит startup в integration-сценариях с
// реальным RabbitMQ. Чистое решение — изолировать health от DI до Этапа 8.
var noProbe = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
};
app.MapHealthChecks("/health/live", noProbe);
app.MapHealthChecks("/health/ready", noProbe);

app.MapControllers();

Log.Information("ProcessingGateway started, environment={Environment}", app.Environment.EnvironmentName);

app.Run();

// Для WebApplicationFactory<Program> в интеграционных тестах.
public partial class Program;
