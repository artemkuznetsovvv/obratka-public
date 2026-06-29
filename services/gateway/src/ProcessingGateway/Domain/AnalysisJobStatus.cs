namespace ProcessingGateway.Domain;

/// ФСМ analysis_jobs.status. ADR-004 §4 + CLAUDE.md (без `language_detection` —
/// решение Этапа 0 №2).
public enum AnalysisJobStatus
{
    Pending,
    Collecting,
    SentToLlm,
    ComputingAggregates,
    Completed,
    Partial,
    Failed
}

public static class AnalysisJobStatusExtensions
{
    private static readonly Dictionary<AnalysisJobStatus, string> ToWireMap = new()
    {
        [AnalysisJobStatus.Pending] = "pending",
        [AnalysisJobStatus.Collecting] = "collecting",
        [AnalysisJobStatus.SentToLlm] = "sent_to_llm",
        [AnalysisJobStatus.ComputingAggregates] = "computing_aggregates",
        [AnalysisJobStatus.Completed] = "completed",
        [AnalysisJobStatus.Partial] = "partial",
        [AnalysisJobStatus.Failed] = "failed"
    };

    private static readonly Dictionary<string, AnalysisJobStatus> FromWireMap =
        ToWireMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string ToWire(this AnalysisJobStatus status) => ToWireMap[status];

    public static AnalysisJobStatus FromWire(string wire) =>
        FromWireMap.TryGetValue(wire, out var status)
            ? status
            : throw new ArgumentException($"Unknown analysis job status: '{wire}'", nameof(wire));
}
