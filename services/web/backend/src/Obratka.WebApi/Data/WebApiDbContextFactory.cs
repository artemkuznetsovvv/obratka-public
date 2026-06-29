using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Obratka.WebApi.Data;

internal sealed class WebApiDbContextFactory : IDesignTimeDbContextFactory<WebApiDbContext>
{
    public WebApiDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("WebApiDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:WebApiDb is required for design-time migration. " +
                "Set it in appsettings.Development.json or via env var.");

        var options = new DbContextOptionsBuilder<WebApiDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WebApiDbContext(options);
    }
}
