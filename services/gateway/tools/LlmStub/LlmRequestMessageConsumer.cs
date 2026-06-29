using System.Text.Json;
using MassTransit;
using ProcessingGateway.Application.Llm;
using ProcessingGateway.Application.Messaging.Contracts;
using ProcessingGateway.Infrastructure.Storage;
using LogContext = Serilog.Context.LogContext;

namespace ProcessingGateway.LlmStub;

/// LLM-стуб (schema 2.0): имитирует внешний LLM-сервис.
///
/// Слушает `Llm__RequestQueue`, читает `input.json`, синтезирует:
///   - `output_reviews.json` — per-review aspect-based анализ (наивные эвристики)
///   - `output_summary.json` — job-level summary + 3 заглушечные рекомендации
///
/// Публикует **raw JSON** `LlmResultMessage` в `Llm__ResultQueue` (без MT envelope) —
/// тем же транспортом, что использует реальный LLM-сервис, см. LLM_PYTHON_QUICKSTART.md §4.
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

            var reviews = input.Reviews.Select(SynthesizeReviewResult).ToList();
            var reviewsOutput = new LlmReviewsOutput(
                SchemaVersion: msg.SchemaVersion,
                AnalysisJobId: msg.AnalysisJobId,
                Reviews: reviews);
            await _blob.WriteReviewsOutputAsync(msg.AnalysisJobId, reviewsOutput, context.CancellationToken);

            var recommendations = BuildRecommendations(reviews);
            var summary = BuildSummary(reviews);
            var summaryOutput = new LlmSummaryOutput(
                SchemaVersion: msg.SchemaVersion,
                AnalysisJobId: msg.AnalysisJobId,
                RecommendationsCount: recommendations.Count,
                Summary: summary,
                FullRecommendations: recommendations);
            await _blob.WriteSummaryOutputAsync(msg.AnalysisJobId, summaryOutput, context.CancellationToken);

            var (bucket, _) = S3UrlParser.Parse(msg.PayloadUrl);
            var reviewsUrl = $"s3://{bucket}/{msg.AnalysisJobId}/output_reviews.json";
            var summaryUrl = $"s3://{bucket}/{msg.AnalysisJobId}/output_summary.json";

            await context.Publish(new LlmResultMessage(
                AnalysisJobId: msg.AnalysisJobId,
                Status: "finished",
                ResultReviewsUrl: reviewsUrl,
                ResultSummaryUrl: summaryUrl,
                SchemaVersion: msg.SchemaVersion,
                Error: null), context.CancellationToken);

            _logger.LogInformation(
                "LLM stub finished: {ReviewsCount} reviews + {RecsCount} recs → {ReviewsUrl}, {SummaryUrl}",
                reviews.Count, recommendations.Count, reviewsUrl, summaryUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM stub failed for job {AnalysisJobId}", msg.AnalysisJobId);
            await context.Publish(new LlmResultMessage(
                AnalysisJobId: msg.AnalysisJobId,
                Status: "failed",
                ResultReviewsUrl: null,
                ResultSummaryUrl: null,
                SchemaVersion: msg.SchemaVersion,
                Error: ex.Message), context.CancellationToken);
        }
    }

    // --- эвристики (schema 2.0) ---

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

    private static LlmReviewResult SynthesizeReviewResult(LlmInputReview r)
    {
        var lower = r.Text.ToLowerInvariant();
        var posHits = PositiveMarkers.Count(m => lower.Contains(m));
        var negHits = NegativeMarkers.Count(m => lower.Contains(m));

        string overallSentiment;
        double overallConfidence;
        if (posHits > negHits && posHits > 0)
        {
            overallSentiment = "позитивный";
            overallConfidence = Math.Min(0.6 + 0.1 * posHits, 0.95);
        }
        else if (negHits > posHits && negHits > 0)
        {
            overallSentiment = "негативный";
            overallConfidence = Math.Min(0.6 + 0.1 * negHits, 0.95);
        }
        else
        {
            overallSentiment = r.Stars switch
            {
                >= 4 => "позитивный",
                3 => "нейтральный",
                <= 2 and > 0 => "негативный",
                _ => "нейтральный"
            };
            overallConfidence = r.Stars.HasValue ? 0.55 : 0.40;
        }

        // Stub aspects: один общий aspect "общее впечатление" с тем же sentiment.
        var aspects = new List<LlmAspect>
        {
            new(Topic: "общее впечатление",
                Sentiment: overallSentiment,
                Confidence: overallConfidence,
                Fragment: "",
                IsFreeform: false)
        };

        return new LlmReviewResult(
            ReviewId: r.ReviewId,
            Text: r.Text,
            OverallSentiment: overallSentiment,
            OverallConfidence: overallConfidence,
            Aspects: aspects);
    }

    private static List<LlmRecommendation> BuildRecommendations(IReadOnlyList<LlmReviewResult> reviews)
    {
        var positive = reviews.Count(r => r.OverallSentiment == "позитивный");
        var negative = reviews.Count(r => r.OverallSentiment == "негативный");
        var total = Math.Max(reviews.Count, 1);

        return new List<LlmRecommendation>
        {
            new(Priority: 1,
                Topic: "качество обслуживания",
                Title: "(stub) Усилить положительные практики",
                Body: $"По {positive * 100 / total}% отзывов виден позитив — масштабировать удачные практики.",
                ExpectedImpact: "Удержание клиентов, рост рейтинга.",
                Evidence: Array.Empty<string>()),
            new(Priority: 2,
                Topic: "обратная связь",
                Title: "(stub) Закрыть негативные кейсы",
                Body: $"{negative * 100 / total}% отзывов негативные — реагировать в течение 24 часов.",
                ExpectedImpact: "Снижение оттока, повышение NPS.",
                Evidence: Array.Empty<string>())
        };
    }

    private static string BuildSummary(IReadOnlyList<LlmReviewResult> reviews)
    {
        var positive = reviews.Count(r => r.OverallSentiment == "позитивный");
        var negative = reviews.Count(r => r.OverallSentiment == "негативный");
        var total = Math.Max(reviews.Count, 1);

        return $"(stub) Анализ {reviews.Count} отзывов: " +
               $"позитив {positive * 100 / total}%, негатив {negative * 100 / total}%. " +
               "Реальные рекомендации появятся при подключении живой LLM.";
    }
}
