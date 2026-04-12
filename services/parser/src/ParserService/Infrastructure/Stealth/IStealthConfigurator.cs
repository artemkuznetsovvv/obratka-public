using Microsoft.Playwright;

namespace ParserService.Infrastructure.Stealth;

public interface IStealthConfigurator
{
    Task ApplyStealthAsync(IBrowserContext browserContext, CancellationToken ct);
}
