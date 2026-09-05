using System;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Mode of detrending / pre-smoothing applied before Hilbert Transform FIR filtering.
/// </summary>
public enum HilbertDetrendMode
{
    Wma4,
    FirDetrender,
    HighPass
}

/// <summary>
/// Configuration parameters for Hilbert Transform Decomposition Engine.
/// </summary>
public sealed record HilbertDecompositionParameters(
    int DefaultPeriod = 14,
    int MinPeriod = 6,
    int MaxPeriod = 50,
    decimal SmoothBeta = 0.1m,
    decimal DeltaLimit = 3.0m,
    int WarmupBars = 50,
    int StabilityWindow = 10,
    double StabilityThresholdDegrees = 15.0,
    decimal MicroAmplitudeThreshold = 1e-10m,
    HilbertDetrendMode DetrendMode = HilbertDetrendMode.FirDetrender)
{
    public void Validate()
    {
        if (DefaultPeriod < MinPeriod || DefaultPeriod > MaxPeriod)
            throw new ArgumentOutOfRangeException(nameof(DefaultPeriod), "DefaultPeriod must be within [MinPeriod, MaxPeriod].");
        if (MinPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(MinPeriod), "MinPeriod must be at least 2.");
        if (MaxPeriod <= MinPeriod)
            throw new ArgumentOutOfRangeException(nameof(MaxPeriod), "MaxPeriod must be greater than MinPeriod.");
        if (SmoothBeta <= 0m || SmoothBeta > 1m)
            throw new ArgumentOutOfRangeException(nameof(SmoothBeta), "SmoothBeta must be in (0, 1].");
        if (DeltaLimit <= 0m)
            throw new ArgumentOutOfRangeException(nameof(DeltaLimit), "DeltaLimit must be positive.");
        if (WarmupBars < 7)
            throw new ArgumentOutOfRangeException(nameof(WarmupBars), "WarmupBars must be at least 7 for 7-tap FIR filter.");
        if (StabilityWindow < 2)
            throw new ArgumentOutOfRangeException(nameof(StabilityWindow), "StabilityWindow must be at least 2.");
        if (StabilityThresholdDegrees <= 0.0 || StabilityThresholdDegrees > 90.0)
            throw new ArgumentOutOfRangeException(nameof(StabilityThresholdDegrees), "StabilityThresholdDegrees must be in (0, 90].");
        if (MicroAmplitudeThreshold <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MicroAmplitudeThreshold), "MicroAmplitudeThreshold must be positive.");
    }
}

/// <summary>
/// Encapsulates per-bar structured decomposition metrics.
/// </summary>
public readonly record struct HilbertSampleResult(
    decimal InPhase,
    decimal Quadrature,
    decimal Amplitude,
    decimal Power,
    decimal NormalizedInPhase,
    decimal NormalizedQuadrature,
    double PhaseRad,
    double PhaseDeg,
    double UnwrappedPhaseDeg,
    double PhaseDeltaDeg,
    decimal InstantaneousPeriod,
    decimal DominantCycle,
    double CycleStability,
    bool TrendMode,
    bool IsValid,
    bool IsWarmup);

/// <summary>
/// Complete time-series result of Hilbert Transform decomposition.
/// </summary>
public sealed record HilbertDecompositionResult(
    HilbertSampleResult[] Samples,
    int WarmupBars,
    HilbertDecompositionParameters Parameters)
{
    public ReadOnlySpan<HilbertSampleResult> AsSpan() => Samples.AsSpan();
    public HilbertSampleResult this[int index] => Samples[index];
    public int Count => Samples.Length;
}
