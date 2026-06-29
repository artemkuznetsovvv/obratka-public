using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ParserService.Core;
using ParserService.Core.Models;
using ParserService.Infrastructure.Browser;
using ParserService.Infrastructure.Proxy;
using ParserService.Infrastructure.RateLimiting;
using ParserService.Infrastructure.Stealth;
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

            // Replace infrastructure with stubs — no real Playwright/browser/proxy
            RemoveAll<IBrowserPool>(services);
            services.AddSingleton<IBrowserPool, StubBrowserPool>();

            RemoveAll<IProxyRotator>(services);
            services.AddSingleton<IProxyRotator, StubProxyRotator>();

            RemoveAll<IStealthConfigurator>(services);
            services.AddSingleton<IStealthConfigurator, StubStealthConfigurator>();

            RemoveAll<IPerSourceRateLimiter>(services);
            services.AddSingleton<IPerSourceRateLimiter, StubPerSourceRateLimiter>();

            // Replace all plugins with stubs — no real network calls
            RemoveAll<IReviewSourcePlugin>(services);
            services.AddSingleton<IReviewSourcePlugin, StubReviewSourcePlugin>();
        });
    }

    private static void RemoveAll<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
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

/// <summary>
/// Stub plugin for tests — returns empty results, never touches the network.
/// Supports all source types so that any source slug is valid in tests.
/// </summary>
internal class StubReviewSourcePlugin : IReviewSourcePlugin
{
    public SourceType Source => SourceType.YandexMaps;

    public Task<IReadOnlyList<SearchBranchResult>> SearchBranchesAsync(
        CompanySearchRequest request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SearchBranchResult>>([]);

    public Task<IReadOnlyList<RawReview>> FetchReviewsAsync(
        BranchTarget branch, DateRange period, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RawReview>>([]);
}
