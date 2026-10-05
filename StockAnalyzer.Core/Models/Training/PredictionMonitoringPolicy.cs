using System;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>The domain constants and pure formulas of prediction monitoring (tunable cadences live in PredictionMonitoringSettings).</summary>
public static class PredictionMonitoringPolicy
{
    public const int SchemaVersion = 1;
    public const int LookbackCalendarDays = 30;
    public const int MinimumMaturedCount = 30;
    public const decimal DecayThreshold = 0.50m;
    public const int SchedulePeriodDays = 7;
    // The loop cadences (scheduler poll, lease retry, health evaluation) are tunable and live in PredictionMonitoringSettings.

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
