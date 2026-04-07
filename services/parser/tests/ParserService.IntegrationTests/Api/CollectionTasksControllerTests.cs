using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace ParserService.IntegrationTests.Api;

public class CollectionTasksControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public CollectionTasksControllerTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Search_WithValidRequest_Returns200AndResults()
    {
        var request = new { query = "Test", city = "Moscow", sources = new[] { "2gis", "yandex" } };

        var response = await _client.PostAsJsonAsync(
            "/api/collection-tasks/search", request, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_WithValidRequest_Returns202WithTaskId()
    {
        var request = new
        {
            job_id = Guid.NewGuid(),
            company_id = Guid.NewGuid(),
            source = "2gis",
            branches = new[]
            {
                new
                {
                    branch_id = Guid.NewGuid(),
                    external_id = "123456",
                    external_url = "https://2gis.ru/firm/123456"
                }
            }
        };

        var response = await _client.PostAsJsonAsync(
            "/api/collection-tasks", request, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("task_id").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_WithInvalidSource_Returns400()
    {
        var request = new
        {
            job_id = Guid.NewGuid(),
            company_id = Guid.NewGuid(),
            source = "unknown_source",
            branches = new[]
            {
                new
                {
                    branch_id = Guid.NewGuid(),
                    external_id = "123",
                    external_url = "https://example.com"
                }
            }
        };

        var response = await _client.PostAsJsonAsync(
            "/api/collection-tasks", request, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStatus_WithExistingTask_Returns200()
    {
        // Create a task first
        var createRequest = new
        {
            job_id = Guid.NewGuid(),
            company_id = Guid.NewGuid(),
            source = "2gis",
            branches = new[]
            {
                new
                {
                    branch_id = Guid.NewGuid(),
                    external_id = "789",
                    external_url = "https://2gis.ru/firm/789"
                }
            }
        };

        var createResponse = await _client.PostAsJsonAsync(
            "/api/collection-tasks", createRequest, JsonOptions);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var taskId = createBody.GetProperty("task_id").GetGuid();

        // Get status
        var response = await _client.GetAsync($"/api/collection-tasks/{taskId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        body.GetProperty("task_id").GetGuid().Should().Be(taskId);
        body.GetProperty("source").GetString().Should().Be("2gis");
    }

    [Fact]
    public async Task GetStatus_WithNonExistentTask_Returns404()
    {
        var response = await _client.GetAsync($"/api/collection-tasks/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
