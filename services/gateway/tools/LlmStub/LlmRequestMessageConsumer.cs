using MassTransit;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Infrastructure.Storage;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.LlmStub;

/// LLM-стуб: имитирует внешний LLM-сервис из ADR-004. Слушает `Llm__RequestQueue`,
/// читает `input.json` из S3, синтезирует наивный output и публикует `LlmResultMessage`
/// в `Llm__ResultQueue`. Полностью совместим с реальным LLM по контракту:
/// PG ничего не должен знать о том, кто на другой стороне очереди — стуб или живой сервис.
///
/// Эвристики максимально простые (текст ↔ ключевые слова, fallback по stars). Цель —
/// дать осмысленные данные для разработки фронтенда и тестирования pipeline-ингеста,
/// не подменять реальную LLM.
public sealed class LlmRequestMessageConsumer : IConsumer<LlmRequestMessage>
{
    private readonly IJobBlobStorage _blob;
    private readonly ILogger<LlmRequestMessageConsumer> _logger;

    public LlmRequestMessageConsumer(IJobBlobStorage blob, ILogger<LlmRequestMessageConsumer> logger)
    {
        _blob = blob;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<LlmRequestMessage> context)
    {
        var msg = context.Message;
        using var _ = LogContext.PushProperty("AnalysisJobId", msg.AnalysisJobId);

        _logger.LogInformation(
            "LlmRequest received: payload={PayloadUrl}, reviews={ReviewCount}, schema={SchemaVersion}",
            msg.PayloadUrl, msg.ReviewCount, msg.SchemaVersion);

        try
        {
            var input = await _blob.ReadInputAsync(msg.AnalysisJobId, context.CancellationToken);

            var processed = input.Reviews.Select(SynthesizeProcessedReview).ToList();
            var output = new LlmOutput(
                SchemaVersion: msg.SchemaVersion,
                AnalysisJobId: msg.AnalysisJobId,
                Recommendation: BuildRecommendation(processed),
                ProcessedReview: processed);

            await _blob.WriteOutputAsync(msg.AnalysisJobId, output, context.CancellationToken);

            // Парсим bucket из payloadUrl чтобы построить такой же result_url —
            // у LLM могут быть свои bucket-настройки, но в нашем стубе тот же bucket.
            var (bucket, _) = S3UrlParser.Parse(msg.PayloadUrl);
            var resultUrl = $"s3://{bucket}/{msg.AnalysisJobId}/output.json";

            await context.Publish(new LlmResultMessage(
                AnalysisJobId: msg.AnalysisJobId,
                Status: "finished",
                ResultUrl: resultUrl,
                SchemaVersion: msg.SchemaVersion,
                Error: null), context.CancellationToken);

            _logger.LogInformation(
                "LLM stub finished: {ProcessedCount} reviews → {ResultUrl}",
                processed.Count, resultUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM stub failed for job {AnalysisJobId}", msg.AnalysisJobId);
            await context.Publish(new LlmResultMessage(
                AnalysisJobId: msg.AnalysisJobId,
                Status: "failed",
                ResultUrl: null,
                SchemaVersion: msg.SchemaVersion,
                Error: ex.Message), context.CancellationToken);
        }
    }

    // --- эвристики ---

    private static readonly string[] PositiveMarkers =
    {
        "отлично", "хорошо", "рекомендую", "спасибо", "удобн", "отзывчив", "доволен",
        "👍", "👌", "❤", "great", "good", "excellent", "thanks"
    };

    private static readonly string[] NegativeMarkers =
    {
        "плохо", "ужасно", "не рекомендую", "грубо", "хамство", "обман", "долго",
        "👎", "😠", "bad", "terrible", "awful"
    };

    private static LlmProcessedReview SynthesizeProcessedReview(LlmInputReview r)
    {
        var lower = r.Text.ToLowerInvariant();
        var posHits = PositiveMarkers.Count(m => lower.Contains(m));
        var negHits = NegativeMarkers.Count(m => lower.Contains(m));

        string sentiment;
        double confidence;
        if (posHits > negHits && posHits > 0)
        {
            sentiment = posHits >= 2 ? "very_positive" : "positive";
            confidence = Math.Min(0.6 + 0.1 * posHits, 0.95);
        }
        else if (negHits > posHits && negHits > 0)
        {
            sentiment = negHits >= 2 ? "very_negative" : "negative";
            confidence = Math.Min(0.6 + 0.1 * negHits, 0.95);
        }
        else
        {
            sentiment = r.Stars switch
            {
                5 => "very_positive",
                4 => "positive",
                3 => "neutral",
                2 => "negative",
                1 => "very_negative",
                _ => "neutral"
            };
            confidence = r.Stars.HasValue ? 0.55 : 0.40;
        }

        return new LlmProcessedReview(
            ReviewId: r.ReviewId,
            FakeStatus: "normal",
            FakeReasonTags: Array.Empty<string>(),
            Sentiment: sentiment,
            SentimentConfidence: confidence,
            IsSpam: false,
            SpamConfidence: 0.05,
            Topics: Array.Empty<string>());
    }

    private static string BuildRecommendation(IReadOnlyList<LlmProcessedReview> processed)
    {
        var positive = processed.Count(p => p.Sentiment is "positive" or "very_positive");
        var negative = processed.Count(p => p.Sentiment is "negative" or "very_negative");
        var total = Math.Max(processed.Count, 1);

        return $"(stub) Анализ {processed.Count} отзывов: " +
               $"позитив {positive * 100 / total}%, негатив {negative * 100 / total}%. " +
               "Реальные рекомендации появятся, когда подключится живая LLM.";
    }
}
