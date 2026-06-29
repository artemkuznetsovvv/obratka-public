using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProcessingGateway.Tests.Infrastructure;

/// WebApplicationFactory с подменой `ConnectionStrings:ProcessingDb` на эфемерный
/// Postgres из PgFixture. Минимальный Program.cs читает конфиг сразу после CreateBuilder,
/// поэтому ConfigureAppConfiguration уже опаздывает — используем ENV vars, они подхватываются
/// первой `EnvironmentVariablesConfigurationProvider` в DefaultConfiguration.
public class ProcessingGatewayFactory : WebApplicationFactory<Program>
{
    public sealed class Settings
    {
        public required string ConnectionString { get; init; }
        public string ParserBaseUrl { get; init; } = "http://parser-stub";
        public string S3Endpoint { get; init; } = "http://minio-stub";
        public string S3AccessKey { get; init; } = "stub";
        public string S3SecretKey { get; init; } = "stub";
        public string S3BucketName { get; init; } = "obratka-jobs";
        public string RabbitHost { get; init; } = "localhost-rabbit-stub";
        public int? RabbitPort { get; init; } = null;       // если null — Program.cs возьмёт default 5672
        public string RabbitUser { get; init; } = "guest";
        public string RabbitPass { get; init; } = "guest";
        public int PollIntervalSeconds { get; init; } = 4;
    }

    public ProcessingGatewayFactory(Settings settings)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__ProcessingDb", settings.ConnectionString);
        Environment.SetEnvironmentVariable("Seq__ServerUrl", "");
        Environment.SetEnvironmentVariable("Parser__BaseUrl", settings.ParserBaseUrl);
        Environment.SetEnvironmentVariable("Parser__PollIntervalSeconds", settings.PollIntervalSeconds.ToString());
        Environment.SetEnvironmentVariable("Parser__TaskTimeoutMinutes", "90");
        Environment.SetEnvironmentVariable("S3__Endpoint", settings.S3Endpoint);
        Environment.SetEnvironmentVariable("S3__AccessKey", settings.S3AccessKey);
        Environment.SetEnvironmentVariable("S3__SecretKey", settings.S3SecretKey);
        Environment.SetEnvironmentVariable("S3__BucketName", settings.S3BucketName);
        Environment.SetEnvironmentVariable("RabbitMq__Host", settings.RabbitHost);
        Environment.SetEnvironmentVariable("RabbitMq__Port", settings.RabbitPort?.ToString() ?? "");
        Environment.SetEnvironmentVariable("RabbitMq__User", settings.RabbitUser);
        Environment.SetEnvironmentVariable("RabbitMq__Pass", settings.RabbitPass);
        Environment.SetEnvironmentVariable("Llm__RequestQueue", "llm.requests");
        Environment.SetEnvironmentVariable("Llm__ResultQueue", "llm.results");
        Environment.SetEnvironmentVariable("Gateway__ApiKey", "");  // в Development не проверяется
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }

    /// Удобный конструктор для smoke-тестов (PgFixture only).
    public ProcessingGatewayFactory(string connectionString)
        : this(new Settings { ConnectionString = connectionString }) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Доп. подменять не нужно — ENV vars выше уже всё определяют.
    }
}
