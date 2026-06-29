using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace ParserService.Infrastructure.Stealth;

public class StubStealthConfigurator : IStealthConfigurator
{
    private readonly ILogger<StubStealthConfigurator> _logger;

    public StubStealthConfigurator(ILogger<StubStealthConfigurator> logger)
    {
        _logger = logger;
    }

    public Task ApplyStealthAsync(IBrowserContext browserContext, CancellationToken ct)
        => ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);

    public Task ApplyStealthAsync(IBrowserContext browserContext, StealthProfile profile, CancellationToken ct)
    {
        _logger.LogDebug("Stub stealth (profile: {Profile}) — no patches applied", profile);
        return Task.CompletedTask;
    }
}
