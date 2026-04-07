using Microsoft.Extensions.Logging;

namespace ParserService.Infrastructure.Stealth;

public class StubStealthConfigurator : IStealthConfigurator
{
    private readonly ILogger<StubStealthConfigurator> _logger;

    public StubStealthConfigurator(ILogger<StubStealthConfigurator> logger)
    {
        _logger = logger;
    }

    public Task ApplyStealthAsync(object browserContext, CancellationToken ct)
    {
        _logger.LogDebug("Stealth configuration is not implemented yet");
        return Task.CompletedTask;
    }
}
