using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcessingGateway.Api;
using ProcessingGateway.Domain;
using ProcessingGateway.Tests.Infrastructure;

namespace ProcessingGateway.Tests;

[Collection("Postgres")]
public class AnalysisStatusApiTests : IClassFixture<AnalysisStatusApiTests.FactoryHolder>
{
    private readonly PgFixture _pg;
    private readonly ProcessingGatewayFactory _factory;

    public AnalysisStatusApiTests(PgFixture pg, FactoryHolder holder)
    {
        _pg = pg;
        _factory = holder.Get(pg.ConnectionString);
    }

    [Fact]
    public async Task Returns_404_for_unknown_job()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analyses/{Guid.NewGuid()}/status");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(AnalysisJobStatus.Pending,             "pending",   "pending",   "pending")]
    [InlineData(AnalysisJobStatus.Collecting,          "active",    "pending",   "pending")]
    [InlineData(AnalysisJobStatus.SentToLlm,           "completed", "active",    "pending")]
    [InlineData(AnalysisJobStatus.ComputingAggregates, "completed", "completed", "active")]
    [InlineData(AnalysisJobStatus.Completed,           "completed", "completed", "completed")]
    [InlineData(AnalysisJobStatus.Partial,             "completed", "completed", "completed")]
    [InlineData(AnalysisJobStatus.Failed,              "failed",    "failed",    "failed")]
    public async Task Stage_states_match_FSM_status(
        AnalysisJobStatus status,
        string expectedCollecting,
        string expectedLlm,
        string expectedDashboard)
    {
        var jobId = await SeedJobAsync(status);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/analyses/{jobId}/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<AnalysisStatusController.StatusResponse>();
        body!.Status.Should().Be(status.ToWire());

        var byKey = body.Stages.ToDictionary(s => s.Key);
        byKey["collecting"].State.Should().Be(expectedCollecting);
        byKey["llm_analysis"].State.Should().Be(expectedLlm);
        byKey["building_dashboard"].State.Should().Be(expectedDashboard);
    }

    [Fact]
    public async Task Sources_progress_is_rendered_per_source()
    {
        var jobId = Guid.NewGuid();
        await using (var ctx = _pg.NewDbContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM analysis_jobs WHERE id = {0}", jobId);
            ctx.AnalysisJobs.Add(new AnalysisJob
            {
                Id = jobId,
                CompanyId = Guid.NewGuid(),
                Status = AnalysisJobStatus.Collecting,
                ReviewCount = 143,
                CollectionProgress = new Dictionary<string, CollectionProgressEntry>
                {
                    ["yandex"] = new() { Status = "completed", Progress = 100, ReviewCount = 143 },
                    ["2gis"]   = new() { Status = "running",   Progress = 60 }
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<AnalysisStatusController.StatusResponse>(
            $"/api/analyses/{jobId}/status");

        body!.ReviewCount.Should().Be(143);
        body.Sources.Should().HaveCount(2);
        body.Sources["yandex"].Status.Should().Be("completed");
        body.Sources["yandex"].Progress.Should().Be(100);
        body.Sources["yandex"].ReviewCount.Should().Be(143);
        body.Sources["2gis"].Status.Should().Be("running");
        body.Sources["2gis"].Progress.Should().Be(60);
        body.Sources["2gis"].ReviewCount.Should().BeNull();
    }

    private async Task<Guid> SeedJobAsync(AnalysisJobStatus status)
    {
        var jobId = Guid.NewGuid();
        await using var ctx = _pg.NewDbContext();
        ctx.AnalysisJobs.Add(new AnalysisJob
        {
            Id = jobId,
            CompanyId = Guid.NewGuid(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await ctx.SaveChangesAsync();
        return jobId;
    }

    public class FactoryHolder : IDisposable
    {
        private ProcessingGatewayFactory? _factory;
        public ProcessingGatewayFactory Get(string connectionString)
            => _factory ??= new ProcessingGatewayFactory(connectionString);
        public void Dispose() => _factory?.Dispose();
    }
}
