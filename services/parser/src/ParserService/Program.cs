using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Stealth;
using ParserService.Infrastructure.Storage;
using ParserService.Infrastructure.Telemetry;
using ParserService.Sources.GoogleMaps;
using ParserService.Sources.TwoGis;
using ParserService.Sources.YandexMaps;
using Serilog;

// ReSharper disable RedundantUsingDirective

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "parser-service")
        // MachineName через WithProperty (а не WithMachineName) — без пакета Serilog.Enrichers.Environment;
        // значение константно на процесс, эффект тот же. ADR-008 §template.
        .Enrich.WithProperty("MachineName", Environment.MachineName)
        .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}");

    var seqUrl = ctx.Configuration["Seq:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(seqUrl))
    {
        cfg.WriteTo.Seq(seqUrl, apiKey: ctx.Configuration["Seq:ApiKey"]);
    }
});

// --- SQLite + EF Core ---
builder.Services.AddDbContext<ParserDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("TasksDb")
        ?? "Data Source=tasks.db"));

// --- AWS S3 (MinIO compatible) ---
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var config = new AmazonS3Config
    {
        ServiceURL = builder.Configuration["S3:Endpoint"] ?? "http://localhost:9000",
        ForcePathStyle = true
    };
    var credentials = new BasicAWSCredentials(
        builder.Configuration["S3:AccessKey"] ?? "minioadmin",
        builder.Configuration["S3:SecretKey"] ?? "minioadmin");
    return new AmazonS3Client(credentials, config);
});

// --- Infrastructure ---
if (builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IS3ResultStorage, LocalFileResultStorage>();
else
    builder.Services.AddSingleton<IS3ResultStorage, S3ResultStorage>();
builder.Services.Configure<BrowserPoolOptions>(
    builder.Configuration.GetSection(BrowserPoolOptions.SectionName));
builder.Services.AddSingleton<IBrowserPool, PlaywrightBrowserPool>();
builder.Services.Configure<ProxyOptions>(
    builder.Configuration.GetSection(ProxyOptions.SectionName));
builder.Services.AddScoped<IProxyRepository, SqliteProxyRepository>();
builder.Services.AddSingleton<IProxyRotator, DbProxyRotator>();
builder.Services.AddSingleton<IStealthConfigurator, PlaywrightStealthConfigurator>();
builder.Services.Configure<RateLimitingOptions>(
    builder.Configuration.GetSection(RateLimitingOptions.SectionName));
builder.Services.AddSingleton<IPerSourceRateLimiter, PerSourceRateLimiter>();

// --- YandexMaps configuration ---
builder.Services.Configure<YandexMapsOptions>(
    builder.Configuration.GetSection(YandexMapsOptions.SectionName));

// --- TwoGis configuration ---
builder.Services.Configure<TwoGisOptions>(
    builder.Configuration.GetSection(TwoGisOptions.SectionName));
builder.Services.AddHttpClient("2gis");

// --- GoogleMaps configuration ---
builder.Services.Configure<GoogleMapsOptions>(
    builder.Configuration.GetSection(GoogleMapsOptions.SectionName));

// --- Core ---
builder.Services.AddScoped<ITaskRepository, SqliteTaskRepository>();
builder.Services.AddScoped<CollectionTaskOrchestrator>();

// --- Plugins ---
builder.Services.AddSingleton<IReviewSourcePlugin, TwoGisPlugin>();
builder.Services.AddSingleton<IReviewSourcePlugin, YandexMapsPlugin>();
builder.Services.AddSingleton<IReviewSourcePlugin, GoogleMapsPlugin>();

// --- Background task processing ---
builder.Services.AddSingleton<TaskQueue>();
builder.Services.Configure<WorkersOptions>(
    builder.Configuration.GetSection(WorkersOptions.SectionName));
builder.Services.AddHostedService<CollectionTaskBackgroundService>();

// --- Controllers + JSON ---
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.Converters.Add(new SourceTypeJsonConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddOpenApi();

var app = builder.Build();

// --- Auto-migrate SQLite on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ParserDbContext>();
    if (db.Database.GetPendingMigrations().Any())
        await db.Database.MigrateAsync();
    else
        await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Correlation — до request-logging (строка request-summary пишется уже после возврата из downstream).
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        if (ex is not null || httpContext.Response.StatusCode >= 500) return Serilog.Events.LogEventLevel.Error;
        if (httpContext.Response.StatusCode >= 400) return Serilog.Events.LogEventLevel.Warning;
        if (elapsed > 1000) return Serilog.Events.LogEventLevel.Warning;
        return Serilog.Events.LogEventLevel.Debug;
    };
    options.EnrichDiagnosticContext = (diag, httpContext) =>
    {
        diag.Set("Direction", "incoming");
        // AnalysisJobId/CompanyId на строку request-summary POST /api/collection-tasks
        // (контроллер кладёт их в Items). Инициатора у Parser нет — это internal-сервис.
        if (httpContext.Items.TryGetValue("AnalysisJobId", out var jobId) && jobId is not null)
            diag.Set("AnalysisJobId", jobId);
        if (httpContext.Items.TryGetValue("CompanyId", out var companyId) && companyId is not null)
            diag.Set("CompanyId", companyId);
    };
});

app.MapControllers();

await app.RunAsync();

public partial class Program { }
