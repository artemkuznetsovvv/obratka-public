namespace Obratka.WebApi.Monitoring;

// Конфиг live-мониторинга (секция "Monitoring" в appsettings).
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    public NegativeSpikeOptions NegativeSpike { get; set; } = new();

    // Пороги правила «резкий рост негатива» (ТЗ §6). Выносим в конфиг.
    public sealed class NegativeSpikeOptions
    {
        public double MinIncreasePp { get; set; } = 15;
        public int MinNewReviews { get; set; } = 20;
    }
}
