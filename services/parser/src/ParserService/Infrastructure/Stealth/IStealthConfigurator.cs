using Microsoft.Playwright;

namespace ParserService.Infrastructure.Stealth;

public enum StealthProfile
{
    /// <summary>Webdriver, navigator, chrome runtime, permissions — proven safe.</summary>
    Minimal,

    /// <summary>Minimal + WebGL + canvas noise — stable, tested.</summary>
    Moderate,

    /// <summary>Moderate + AudioContext + font enumeration — experimental.</summary>
    Full
}

public interface IStealthConfigurator
{
    Task ApplyStealthAsync(IBrowserContext browserContext, CancellationToken ct)
        => ApplyStealthAsync(browserContext, StealthProfile.Moderate, ct);

    Task ApplyStealthAsync(IBrowserContext browserContext, StealthProfile profile, CancellationToken ct);
}
