using Amazon.S3;
using MassTransit;
using ProcessingGateway.Infrastructure.Storage;
using ProcessingGateway.LlmStub;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

// Serilog по тому же шаблону, что и основной сервис: Console + Seq, enricher Service.
builder.Services.AddSerilog((services, config) =>
{
    var seqUrl = builder.Configuration["Seq:ServerUrl"];
    var seqApiKey = builder.Configuration["Seq:ApiKey"];

    config
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Service", "LlmStub")
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}");

    if (!string.IsNullOrWhiteSpace(seqUrl))
        config.WriteTo.Seq(seqUrl, apiKey: string.IsNullOrWhiteSpace(seqApiKey) ? null : seqApiKey);
});

// AmazonS3 — те же настройки что у PG (ForcePathStyle обязателен для MinIO).
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var endpoint = cfg["S3:Endpoint"]
        ?? throw new InvalidOperationException("S3:Endpoint is not configured");
    return new AmazonS3Client(
        cfg["S3:AccessKey"]!,
        cfg["S3:SecretKey"]!,
        new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = cfg["S3:Region"] ?? "us-east-1"
        });
});
builder.Services.AddSingleton<IJobBlobStorage, S3JobBlobStorage>();

// MassTransit — без Outbox (стуб не имеет БД). Просто consumer + RabbitMQ.
var rabbitHost = builder.Configuration["RabbitMq:Host"]
    ?? throw new InvalidOperationException("RabbitMq:Host is not configured");
var rabbitPort = ushort.TryParse(builder.Configuration["RabbitMq:Port"], out var p) ? p : (ushort)5672;
var rabbitUser = builder.Configuration["RabbitMq:User"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMq:Pass"] ?? "guest";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<LlmRequestMessageConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, rabbitPort, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });
        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromSeconds(15),
            intervalDelta: TimeSpan.FromSeconds(2)));

        // Стуб публикует LlmResultMessage как raw JSON — точно так же, как реальный LLM-сервис
        // (см. LLM_PYTHON_QUICKSTART.md §4). PG-сторона ожидает raw на `llm.results`.
        // `AnyMessageType` — потому что мы единственный publisher этого типа на этом эндпоинте.
        cfg.UseRawJsonSerializer(RawSerializerOptions.AnyMessageType);

        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
Log.Information("LlmStub started");
host.Run();
