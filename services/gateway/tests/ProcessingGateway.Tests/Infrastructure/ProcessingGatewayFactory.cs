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
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Доп. подменять не нужно — ENV vars выше уже всё определяют.
    }
}
