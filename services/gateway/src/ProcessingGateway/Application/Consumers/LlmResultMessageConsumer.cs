using MassTransit;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Application.Pipeline;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.Application.Consumers;

/// Слушает `Llm__ResultQueue` (raw JSON, без MassTransit envelope).
/// Делегирует ингест в `LlmResultIngestor`.
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
        // CorrelationId-трассировка: используем AnalysisJobId как сквозной идентификатор
        // (LLM-сервис публикует raw JSON без MT envelope, поэтому ConsumeContext.CorrelationId
        // может быть пустым; AnalysisJobId — наш source of truth).
        using var _ = LogContext.PushProperty("CorrelationId", msg.AnalysisJobId);
        using var __ = LogContext.PushProperty("AnalysisJobId", msg.AnalysisJobId);

        _logger.LogInformation(
            "LlmResultMessage received: status={Status}, schema={SchemaVersion}",
            msg.Status, msg.SchemaVersion);

        switch (msg.Status?.ToLowerInvariant())
        {
            case "finished":
                if (string.IsNullOrWhiteSpace(msg.ResultReviewsUrl))
                    throw new InvalidOperationException(
                        $"LLM finished without result_reviews_url for job {msg.AnalysisJobId}");
                if (string.IsNullOrWhiteSpace(msg.ResultSummaryUrl))
                    throw new InvalidOperationException(
                        $"LLM finished without result_summary_url for job {msg.AnalysisJobId}");

                await _ingestor.IngestFinishedAsync(
                    msg.AnalysisJobId,
                    msg.ResultReviewsUrl,
                    msg.ResultSummaryUrl,
                    context.CancellationToken);
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
