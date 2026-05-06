using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Infrastructure.Database;
using ProcessingGateway.Infrastructure.Parser;

namespace ProcessingGateway.Api.Qa;

/// Расширенная диагностика зависимостей. `/health/ready` намеренно minimal
/// (не валит DI graph через MassTransit-bus-health, см. Program.cs); реальные пробы
/// доступны здесь по X-Api-Key.
[ApiController]
[Route("api/qa")]
[RequireQaApiKey]
public sealed class QaDiagnosticsController : ControllerBase
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QaDiagnosticsController> _logger;

    public QaDiagnosticsController(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<QaDiagnosticsController> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("health/dependencies")]
    public async Task<IActionResult> Dependencies(CancellationToken ct)
    {
        var postgres = await ProbeAsync("postgres", () => CheckPostgresAsync(ct));
        var s3 = await ProbeAsync("s3", () => CheckS3Async(ct));
        var parser = await ProbeAsync("parser", () => CheckParserAsync(ct));

        var allOk = postgres.Ok && s3.Ok && parser.Ok;
        return StatusCode(allOk ? 200 : 503, new
        {
            ok = allOk,
            checks = new[] { postgres, s3, parser }
        });
    }

    /// Состояние MassTransit Outbox: сколько unsent, последний sent_at, сколько
    /// in-flight. Помогает понять «сообщение опубликовано, но осело в outbox-таблице».
    [HttpGet("outbox")]
    public async Task<IActionResult> Outbox(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcessingDbContext>();
        await using var conn = await db.Database.GetDbConnection().OpenAsyncIfClosed(ct);

        // outbox_message — таблица MassTransit. Поля: sequence_number, sent_time, message_type
        var unsent = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM outbox_message WHERE inbox_message_id IS NULL");
        var totalInbox = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM inbox_state");
        var lastSent = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(sent_time) FROM outbox_message");

        return Ok(new
        {
            unsent_outbox_count = unsent,
            inbox_state_count = totalInbox,
            last_sent_at = lastSent
        });
    }

    // --- helpers ---

    private static async Task<DependencyProbe> ProbeAsync(string name, Func<Task<(bool ok, string? detail)>> fn)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (ok, detail) = await fn();
            return new DependencyProbe(name, ok, sw.ElapsedMilliseconds, detail, null);
        }
        catch (Exception ex)
        {
            return new DependencyProbe(name, false, sw.ElapsedMilliseconds, null, ex.Message);
        }
    }

    private static IActionResult OkOrUnavailable(bool allOk, object body) => allOk ? new OkObjectResult(body) : new ObjectResult(body) { StatusCode = 503 };

    private async Task<(bool ok, string? detail)> CheckPostgresAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcessingDbContext>();
        var canConnect = await db.Database.CanConnectAsync(ct);
        if (!canConnect) return (false, "CanConnectAsync = false");

        // Дополнительно — версия Postgres через Dapper (EF.SqlQueryRaw<string> требует колонку
        // Value, что неудобно). Версия — приятный бонус для диагностики.
        var connFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await connFactory.OpenAsync(ct);
        var version = await Dapper.SqlMapper.ExecuteScalarAsync<string>(conn, "SELECT version()");
        return (true, version?.Length > 80 ? version[..80] : version);
    }

    private async Task<(bool ok, string? detail)> CheckS3Async(CancellationToken ct)
    {
        var s3 = _services.GetRequiredService<IAmazonS3>();
        var bucket = _configuration["S3:BucketName"]!;

        var head = await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }, ct);
        return (true, $"bucket={bucket}, region={head.Location.Value}");
    }

    private async Task<(bool ok, string? detail)> CheckParserAsync(CancellationToken ct)
    {
        var clientFactory = _services.GetRequiredService<IHttpClientFactory>();
        var http = clientFactory.CreateClient(nameof(IParserClient));
        // Если CreateClient(name) не дал named-клиент — берём typed: его BaseAddress настроен.
        if (http.BaseAddress is null)
        {
            // typed-клиент регистрируется через AddHttpClient<IParserClient,...>
            // и имя по дефолту = тип. Простое fallback: по конфигу.
            var baseUrl = _configuration["Parser:BaseUrl"]!;
            http = clientFactory.CreateClient();
            http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }

        using var response = await http.GetAsync("health/live", ct);
        return (response.IsSuccessStatusCode,
            $"GET {http.BaseAddress}health/live → {(int)response.StatusCode}");
    }

    public record DependencyProbe(string Name, bool Ok, long ElapsedMs, string? Detail, string? Error);
}

internal static class DbConnectionExtensions
{
    public static async Task<System.Data.Common.DbConnection> OpenAsyncIfClosed(
        this System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        return conn;
    }
}
