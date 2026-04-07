using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ParserService.Core;
using ParserService.Infrastructure.Storage;
using ParserService.IntegrationTests.Helpers;

namespace ParserService.IntegrationTests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Remove the real DbContext registration
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ParserDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);

            // Use in-memory SQLite (connection must stay open)
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            services.AddDbContext<ParserDbContext>(options =>
                options.UseSqlite(_connection));

            // Replace S3 with in-memory stub
            var s3Descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IS3ResultStorage));
            if (s3Descriptor != null) services.Remove(s3Descriptor);

            services.AddSingleton<IS3ResultStorage, InMemoryS3ResultStorage>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection?.Dispose();
        }
        base.Dispose(disposing);
    }
}
