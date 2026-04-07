namespace ParserService.Infrastructure.Browser;

public interface IBrowserPool
{
    Task<object> AcquireAsync(CancellationToken ct);
    Task ReleaseAsync(object context);
}
