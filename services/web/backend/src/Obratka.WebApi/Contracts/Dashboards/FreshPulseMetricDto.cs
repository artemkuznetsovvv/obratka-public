namespace Obratka.WebApi.Contracts.Dashboards;

// Метрика 4 «Свежий пульс». Окно жёстко 30 дней от now, period дашборда
// игнорируется. Индекс ∈ [-100, +100] либо null, если в окне нет отзывов с
// непустым overall_sentiment.
//
// previous.Index=null → строка динамики на фронте показывает «—».
public sealed record FreshPulseMetricDto(
    FreshPulseWindowDto Current,
    FreshPulseWindowDto Previous);

public sealed record FreshPulseWindowDto(
    double? Index,
    long Positive,
    long Neutral,
    long Negative,
    long TotalNonEmpty,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive);
