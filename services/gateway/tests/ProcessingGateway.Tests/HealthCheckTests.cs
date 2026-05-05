using System.Net;
using FluentAssertions;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Postgres")]
public class HealthCheckTests : IClassFixture<HealthCheckTests.FactoryHolder>
{
    private readonly ProcessingGatewayFactory _factory;

    public HealthCheckTests(PgFixture pg, FactoryHolder holder)
    {
        _factory = holder.Get(pg.ConnectionString);
    }

    [Fact]
    public async Task Liveness_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_passes_when_postgres_is_reachable()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_back_when_provided()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "test-corr-id-123");

        var response = await client.SendAsync(request);
        response.Headers.GetValues("X-Correlation-ID").Should().Contain("test-corr-id-123");
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/live");
        response.Headers.Should().ContainKey("X-Correlation-ID");
        response.Headers.GetValues("X-Correlation-ID").Single().Should().NotBeNullOrWhiteSpace();
    }

    /// Один WebApplicationFactory на тестовый класс — чтобы Program.cs (и MigrateAsync)
    /// поднялись один раз. Контейнер уже мигрирован PgFixture; повторный MigrateAsync
    /// внутри Program.cs идемпотентен.
    public class FactoryHolder : IDisposable
    {
        private ProcessingGatewayFactory? _factory;

        public ProcessingGatewayFactory Get(string connectionString)
            => _factory ??= new ProcessingGatewayFactory(connectionString);

        public void Dispose() => _factory?.Dispose();
    }
}
