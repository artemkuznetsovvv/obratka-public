using MassTransit;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Application.Pipeline;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Consumers;

/// Слушает `Llm__ResultQueue`. Делегирует ингест в `LlmResultIngestor`.
/// Тонкий consumer-shim — вся логика в Pipeline-сервисе для единообразия с
/// AnalysisOrchestrator/LlmDispatcher и для удобства unit-тестов.
public sealed class LlmResultMessageConsumer : IConsumer<LlmResultMessage>
{
    private readonly LlmResultIngestor _ingestor;
    private readonly ILogger<LlmResultMessageConsumer> _logger;

    public LlmResultMessageConsumer(LlmResultIngestor ingestor, ILogger<LlmResultMessageConsumer> logger)
    {
        _ingestor = ingestor;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<LlmResultMessage> context)
    {
        var msg = context.Message;
        using var _ = LogContext.PushProperty("AnalysisJobId", msg.AnalysisJobId);

        _logger.LogInformation(
            "LlmResultMessage received: status={Status}, schema={SchemaVersion}",
            msg.Status, msg.SchemaVersion);

        switch (msg.Status?.ToLowerInvariant())
        {
            case "finished":
                if (string.IsNullOrWhiteSpace(msg.ResultUrl))
                    throw new InvalidOperationException(
                        $"LLM finished without result_url for job {msg.AnalysisJobId}");
                await _ingestor.IngestFinishedAsync(msg.AnalysisJobId, msg.ResultUrl, context.CancellationToken);
                break;

            case "failed":
                await _ingestor.IngestFailedAsync(msg.AnalysisJobId, msg.Error, context.CancellationToken);
                break;

            default:
                _logger.LogWarning(
                    "Unknown LLM status '{Status}' for job {AnalysisJobId} — ignoring",
                    msg.Status, msg.AnalysisJobId);
                break;
        }
    }
}
