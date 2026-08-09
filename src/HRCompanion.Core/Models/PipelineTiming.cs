namespace HRCompanion.Core.Models;

public sealed record PipelineTiming(
    Guid TurnId,
    DateTimeOffset HrFinalTurnAt,
    DateTimeOffset ActualTurnPersistedAt,
    DateTimeOffset RetrievalStartedAt,
    DateTimeOffset RetrievalCompletedAt,
    DateTimeOffset? AnalysisStartedAt,
    DateTimeOffset? AnalysisCompletedAt,
    DateTimeOffset AnswerRequestStartedAt,
    DateTimeOffset ResponseCompletedAt,
    DateTimeOffset? FirstUsefulRenderedAt = null)
{
    public double FirstUsefulLatencyMs =>
        (FirstUsefulRenderedAt ?? ResponseCompletedAt).Subtract(HrFinalTurnAt).TotalMilliseconds;
    public double FullResponseLatencyMs => ResponseCompletedAt.Subtract(HrFinalTurnAt).TotalMilliseconds;
}

public sealed record LatencySummary(int Count, double MedianMs, double P95Ms)
{
    public static LatencySummary? Calculate(IEnumerable<PipelineTiming> samples)
    {
        var values = samples.Select(sample => sample.FirstUsefulLatencyMs).Order().ToArray();
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        var median = values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        var p95Index = Math.Max(0, (int)Math.Ceiling(values.Length * 0.95) - 1);
        return new(values.Length, median, values[p95Index]);
    }
}

public sealed record AssistanceRunResult(AssistantResponse Response, PipelineTiming? Timing);
