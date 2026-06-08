using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Obratka.Modules.Analytics;
using Obratka.Modules.Notifications;
using Obratka.Modules.Reports;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Companies;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ParserService;
using Obratka.WebApi.Integration.ProcessingGateway;
using Obratka.WebApi.Notifications;
using Obratka.WebApi.Scheduling;
using Serilog;
using Serilog.Events;

const string FrontendCorsPolicy = "FrontendDev";

var builder = WebApplication.CreateBuilder(args);

// Local-only overrides (gitignored) — твои dev-стенды Parser-Service / Processing-Gateway
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

// ---- Options ----
builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<ParserServiceOptions>()
    .Bind(builder.Configuration.GetSection(ParserServiceOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<ProcessingGatewayOptions>()
    .Bind(builder.Configuration.GetSection(ProcessingGatewayOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<Obratka.WebApi.Monitoring.MonitoringOptions>()
    .Bind(builder.Configuration.GetSection(Obratka.WebApi.Monitoring.MonitoringOptions.SectionName));

// ---- Database ----
var connectionString = builder.Configuration.GetConnectionString("WebApiDb")
    ?? throw new InvalidOperationException("ConnectionStrings:WebApiDb must be configured");

builder.Services.AddDbContext<WebApiDbContext>(options =>
    options.UseNpgsql(connectionString));

// ---- Hangfire (ADR-005: планировщик live-мониторинга) ----
// Storage — тот же PostgreSQL-инстанс webapi_db (схема hangfire создаётся автоматически).
// Сервер живёт внутри процесса Web API; jobs только публикуют команды/дёргают PG (быстро).
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();

// ---- Identity ----
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<WebApiDbContext>()
    .AddErrorDescriber<RussianIdentityErrorDescriber>()
    .AddDefaultTokenProviders();

// ---- JWT auth ----
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtSecret = jwtSection["Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret must be configured");

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));
    options.AddPolicy("AuthenticatedUser", p => p.RequireAuthenticatedUser());
});

// ---- Application services ----
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
builder.Services.AddScoped<RefreshCookie>();
builder.Services.AddScoped<DbInitializer>();
builder.Services.AddScoped<IBranchSearchService, BranchSearchService>();
builder.Services.AddSingleton<Obratka.WebApi.Companies.Grouping.IBranchGroupingService,
    Obratka.WebApi.Companies.Grouping.BranchGroupingService>();

builder.Services.AddMemoryCache();

builder.Services.AddAnalyticsModule(builder.Configuration);
builder.Services.AddReportsModule();
builder.Services.AddNotificationsModule(builder.Configuration);

// Адресата уведомлений резолвит Web API (доступ к Identity-пользователю + MonitoringConfig).
builder.Services.AddScoped<INotificationRecipientResolver, WebApiNotificationRecipientResolver>();

// Long-poll receiver Telegram (привязка /start) — только если канал сконфигурирован.
var telegramConfigured = (builder.Configuration
    .GetSection(TelegramOptions.SectionName)
    .Get<TelegramOptions>() ?? new TelegramOptions()).IsConfigured;
if (telegramConfigured)
    builder.Services.AddHostedService<TelegramUpdateListener>();

// Сборщик данных PDF-отчёта. Через ActivatorUtilities: опциональные метрик-сервисы
// Analytics не зарегистрированы при пустом ProcessingReadDb — default-параметры дадут
// null (assembler.IsAvailable=false → 503), а не падение DI.
builder.Services.AddScoped<Obratka.WebApi.Reports.ReportDataAssembler>(sp =>
    ActivatorUtilities.CreateInstance<Obratka.WebApi.Reports.ReportDataAssembler>(sp));

// ---- Live-мониторинг (ADR-005) ----
builder.Services.AddScoped<IMonitoringScheduler, MonitoringScheduler>();
// Runner резолвим через ActivatorUtilities: опциональные Analytics-сервисы (stats/recommendations)
// не зарегистрированы при пустом ProcessingReadDb — default-параметры дадут null, а не падение DI.
builder.Services.AddScoped<IMonitoringCycleRunner>(sp =>
    ActivatorUtilities.CreateInstance<MonitoringCycleRunner>(sp));

// Уведомления по разовым анализам: фоновая reconcile-джоба отслеживает запущенные анализы
// и шлёт пинг по готовности (готово — пользователю, ошибка — админу) независимо от UI.
builder.Services.AddScoped<IAnalysisNotificationReconciler, AnalysisNotificationReconciler>();

// ---- Parser-Service HTTP client ----
builder.Services.AddTransient<ParserApiKeyHandler>();
builder.Services.AddHttpClient<IParserServiceClient, ParserServiceClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ParserServiceOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        throw new InvalidOperationException("ParserService:BaseUrl must be configured");
    http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
}).AddHttpMessageHandler<ParserApiKeyHandler>();

// ---- Processing-Gateway HTTP client ----
builder.Services.AddTransient<ProcessingGatewayApiKeyHandler>();
builder.Services.AddHttpClient<IProcessingGatewayClient, ProcessingGatewayClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProcessingGatewayOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.BaseUrl))
        throw new InvalidOperationException("ProcessingGateway:BaseUrl must be configured");
    http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
}).AddHttpMessageHandler<ProcessingGatewayApiKeyHandler>();

// ---- CORS (dev frontend) ----
var frontendOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(frontendOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ---- MVC / OpenAPI ----
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Предупреждаем, если Telegram включён, но не задан DashboardBaseUrl — кнопка «Открыть дашборд» не появится.
var telegramOpts = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramOptions>>().Value;
if (telegramOpts.IsConfigured && string.IsNullOrWhiteSpace(telegramOpts.DashboardBaseUrl))
    app.Logger.LogWarning(
        "[telegram] DashboardBaseUrl не задан — уведомления уйдут без кнопки «Открыть дашборд».");

// ---- DB init + seed ----
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

// ---- Live-мониторинг: регистрация recurring-jobs ----
// Hangfire персистит jobs в БД (переживают рестарт), но переустановка идемпотентна и гарантирует,
// что cron совпадает с текущим конфигом. Reconcile-job — один глобальный.
using (var scope = app.Services.CreateScope())
{
    var scheduler = scope.ServiceProvider.GetRequiredService<IMonitoringScheduler>();
    scheduler.EnsureReconcileJob();

    // Recurring-джоба уведомлений по разовым анализам (каждую минуту, UTC).
    scope.ServiceProvider.GetRequiredService<IRecurringJobManager>().AddOrUpdate<IAnalysisNotificationReconciler>(
        "analysis-notify-reconcile",
        r => r.ReconcileAsync(),
        "* * * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    var db = scope.ServiceProvider.GetRequiredService<WebApiDbContext>();
    var active = await db.MonitoringConfigs
        .Where(m => m.Status == Obratka.WebApi.Monitoring.MonitoringStatus.Active)
        .ToListAsync();
    foreach (var cfg in active)
        scheduler.Register(cfg);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors(FrontendCorsPolicy);
}

app.UseSerilogRequestLogging(options =>
{
    // Уровень per-request. 401/404 — ожидаемый штатный шум (истёкший access-токен перед
    // refresh; поллинг метрик job-а без готовых агрегатов — залп 404 на заход в дашборд),
    // поэтому Debug: в проде (MinimumLevel=Information) не пишутся, в dev (Debug) остаются
    // для отладки роутов. Прочие 4xx — реальные клиентские ошибки → Warning; 5xx/исключения
    // → Error; медленные (>1с) успешные запросы → Warning для видимости перфоманса.
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        var code = httpContext.Response.StatusCode;
        if (ex != null || code >= 500) return LogEventLevel.Error;
        if (code is 401 or 404) return LogEventLevel.Debug;
        if (code >= 400) return LogEventLevel.Warning;
        if (elapsed > 1000) return LogEventLevel.Warning;
        return LogEventLevel.Debug;
    };
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Hangfire Dashboard — только Admin (в Development открыт, см. фильтр).
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireDashboardAuthFilter(app.Environment.IsDevelopment())]
});

app.Run();

// Make Program visible to integration tests (WebApplicationFactory<Program>)
public partial class Program;
