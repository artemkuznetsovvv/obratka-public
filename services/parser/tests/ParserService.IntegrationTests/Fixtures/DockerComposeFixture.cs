using System.Diagnostics;
using System.Net;

namespace ParserService.IntegrationTests.Fixtures;

public class DockerComposeFixture : IAsyncLifetime
{
    private static readonly string ComposeFile = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "docker-compose.tests.yml");

    public async Task InitializeAsync()
    {
        var fullPath = Path.GetFullPath(ComposeFile);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                $"docker-compose.tests.yml not found at {fullPath}");

        // Start containers (uses cached images, only pulls if missing)
        await RunDockerComposeAsync($"-f \"{fullPath}\" up -d --wait");

        // Wait for MinIO to be reachable
        await WaitForMinioAsync(TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        var fullPath = Path.GetFullPath(ComposeFile);
        if (File.Exists(fullPath))
        {
            await RunDockerComposeAsync($"-f \"{fullPath}\" down");
        }
    }

    private static async Task RunDockerComposeAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker compose");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose failed (exit {process.ExitCode}):\n{stderr}\n{stdout}");
        }
    }

    private static async Task WaitForMinioAsync(TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync("http://localhost:9000/minio/health/live");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch
            {
                // Not ready yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("MinIO did not become healthy within timeout");
    }
}
