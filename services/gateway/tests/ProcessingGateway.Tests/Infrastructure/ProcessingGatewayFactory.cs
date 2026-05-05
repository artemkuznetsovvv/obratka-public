using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProcessingGateway.Tests.Infrastructure;

/// WebApplicationFactory с подменой `ConnectionStrings:ProcessingDb` на эфемерный
/// Postgres из PgFixture. Минимальный Program.cs читает конфиг сразу после CreateBuilder,
/// поэтому ConfigureAppConfiguration уже опаздывает — используем ENV vars, они подхватываются
/// первой `EnvironmentVariablesConfigurationProvider` в DefaultConfiguration.
public class ProcessingGatewayFactory : WebApplicationFactory<Program>
{
    public ProcessingGatewayFactory(string connectionString)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__ProcessingDb", connectionString);
        Environment.SetEnvironmentVariable("Seq__ServerUrl", ""); // в тестах Seq не нужен
        // Stub-конфиг для DI: smoke-тесты не дёргают Parser/S3, но Program.cs валидирует
        // эти ключи на старте.
        Environment.SetEnvironmentVariable("Parser__BaseUrl", "http://parser-stub");
        Environment.SetEnvironmentVariable("S3__Endpoint", "http://minio-stub");
        Environment.SetEnvironmentVariable("S3__AccessKey", "stub");
        Environment.SetEnvironmentVariable("S3__SecretKey", "stub");
        Environment.SetEnvironmentVariable("S3__BucketName", "obratka-jobs");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Доп. подменять не нужно — ENV vars выше уже всё определяют.
    }
}
