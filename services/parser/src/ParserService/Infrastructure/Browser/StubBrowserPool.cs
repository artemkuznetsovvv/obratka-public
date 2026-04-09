namespace ParserService.Infrastructure.Browser;

public class StubBrowserPool : IBrowserPool
{
    public Task<object> AcquireAsync(BrowserAcquireOptions? options, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Browser pool not yet implemented. Requires Playwright setup.");
    }

    public Task ReleaseAsync(object context)
    {
        throw new NotImplementedException(
            "Browser pool not yet implemented. Requires Playwright setup.");
    }
}
