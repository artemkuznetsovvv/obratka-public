namespace ParserService.Infrastructure.Stealth;

public interface IStealthConfigurator
{
    Task ApplyStealthAsync(object browserContext, CancellationToken ct);
}
