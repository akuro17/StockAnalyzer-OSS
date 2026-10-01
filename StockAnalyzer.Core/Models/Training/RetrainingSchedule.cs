using System;

namespace StockAnalyzer.Core.Models.Training;

public static class PredictionMonitoringPolicy
{
    public const int SchemaVersion = 1;
    public const int LookbackCalendarDays = 30;
    public const int MinimumMaturedCount = 30;
    public const decimal DecayThreshold = 0.50m;
    public const int SchedulePeriodDays = 7;
    public static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMinutes(1);
    /// <summary>Pause between nonblocking attempts to take the short scheduler root lease when a terminal result must be persisted.</summary>
    public static readonly TimeSpan LeaseRetryInterval = TimeSpan.FromMilliseconds(250);
    /// <summary>Engineering cadence of the application-owned current-health evaluation loop (not a user setting).</summary>
    public static readonly TimeSpan HealthEvaluationInterval = TimeSpan.FromMinutes(1);

    /// <summary>Dimensionless ratio K/N in [0,1] as decimal division; null when no matured result exists (N=0).</summary>
    public static decimal? ComputeAccuracy(int maturedCount, int correctCount)
    {
        ValidateCounts(maturedCount, correctCount);
        return maturedCount == 0 ? null : (decimal)correctCount / maturedCount;
    }

    /// <summary>
    /// Single owner of State=f(N,K): N&lt;minimum is InsufficientData; otherwise K/N below the decay threshold is
    /// Decayed and anything at or above it (equality included) is Healthy.
    /// </summary>
    public static ModelHealthState ComputeHealthState(int maturedCount, int correctCount)
    {
        var accuracy = ComputeAccuracy(maturedCount, correctCount);
        return maturedCount < MinimumMaturedCount ? ModelHealthState.InsufficientData
            : accuracy < DecayThreshold ? ModelHealthState.Decayed : ModelHealthState.Healthy;
    }

    private static void ValidateCounts(int maturedCount, int correctCount)
    {
        if (maturedCount < 0 || correctCount < 0 || correctCount > maturedCount)
            throw new ArgumentOutOfRangeException(nameof(correctCount), "Counts must satisfy 0 <= K <= N.");
    }
}

public enum RetrainingStatus { Disabled, Waiting, Running, Succeeded, Failed, Cancelled, Skipped, Interrupted }

public sealed record RetrainingSchedule(int SchemaVersion, string JobId, TrainingJobConfig Config,
    bool Enabled, DateTimeOffset LastOccurrenceUtc, RetrainingStatus Status,
    string? CandidateModelId = null, string? Error = null);

public sealed record RetrainingOccurrence(string JobId, DateTimeOffset DueUtc, RetrainingStatus Status,
    DateTimeOffset UpdatedUtc, string? RunId = null, string? CandidateModelId = null, string? Error = null);
