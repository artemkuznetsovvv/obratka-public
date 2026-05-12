using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Obratka.Modules.Analytics;
using Obratka.Modules.Notifications;
using Obratka.Modules.Reports;
using Obratka.WebApi.Auth;
using Obratka.WebApi.Data;
using Obratka.WebApi.Integration.ParserService;
using Obratka.WebApi.Integration.ProcessingGateway;
using Serilog;

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

// ---- Database ----
var connectionString = builder.Configuration.GetConnectionString("WebApiDb")
    ?? throw new InvalidOperationException("ConnectionStrings:WebApiDb must be configured");

builder.Services.AddDbContext<WebApiDbContext>(options =>
    options.UseNpgsql(connectionString));

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

builder.Services.AddAnalyticsModule();
builder.Services.AddReportsModule();
builder.Services.AddNotificationsModule();

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

// ---- DB init + seed ----
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors(FrontendCorsPolicy);
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Make Program visible to integration tests (WebApplicationFactory<Program>)
public partial class Program;
