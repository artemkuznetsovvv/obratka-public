using Microsoft.Playwright;

namespace ParserService.Infrastructure.Browser;

public class StubBrowserPool : IBrowserPool
{
    public Task<IBrowserContext> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Browser pool not yet implemented. Requires Playwright setup.");
    }

    public Task ReleaseAsync(IBrowserContext context)
    {
        throw new NotImplementedException(
            "Browser pool not yet implemented. Requires Playwright setup.");
    }
}
