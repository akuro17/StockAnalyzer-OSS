using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Core.Services.Backtest.Evaluation;

public enum DrawdownEpisodeStatus
{
    Available = 0,
    NoDrawdown = 1,
    Unavailable = 2,
    NotComputedPartial = 3,
}

/// <summary>Stable machine reason codes of drawdown episode results and durations (persisted and mapped to localization keys; values must not change).</summary>
internal static class DrawdownReasonCodes
{
    public const string InvalidInput = "InvalidInput";
    public const string ArithmeticOverflow = "ArithmeticOverflow";
    public const string NonFiniteResult = "NonFiniteResult";
    public const string NoDrawdown = "NoDrawdown";
    public const string NotComputedPartial = "NotComputedPartial";
    public const string NonMonotonicTimestamp = "NonMonotonicTimestamp";
}

public sealed class DrawdownDuration
{
    public TimeSpan? Value { get; }
    public string? Reason { get; }

    internal DrawdownDuration(TimeSpan? value, string? reason)
    {
        Value = value;
        Reason = reason;
    }
}

public sealed class DrawdownEpisode
{
    public int PeakIndex { get; }
    public int TroughIndex { get; }
    public int? RecoveryIndex { get; }
    public DateTime PeakUtc { get; }
    public DateTime TroughUtc { get; }
    public DateTime? RecoveryUtc { get; }
    public decimal PeakEquity { get; }
    public decimal TroughEquity { get; }
    public decimal Depth { get; }
    public MetricUnit Unit { get; }
    public int DeclineBars { get; }
    public int? RecoveryBars { get; }
    public int? UnderwaterBars { get; }
    public DrawdownDuration DeclineDuration { get; }
    public DrawdownDuration RecoveryDuration { get; }
    public DrawdownDuration UnderwaterDuration { get; }

    internal DrawdownEpisode(
        int peakIndex,
        int troughIndex,
        int? recoveryIndex,
        DateTime peakUtc,
        DateTime troughUtc,
        DateTime? recoveryUtc,
        decimal peakEquity,
        decimal troughEquity,
        decimal depth,
        MetricUnit unit,
        DrawdownDuration declineDuration,
        DrawdownDuration recoveryDuration,
        DrawdownDuration underwaterDuration)
    {
        PeakIndex = peakIndex;
        TroughIndex = troughIndex;
        RecoveryIndex = recoveryIndex;
        PeakUtc = peakUtc;
        TroughUtc = troughUtc;
        RecoveryUtc = recoveryUtc;
        PeakEquity = peakEquity;
        TroughEquity = troughEquity;
        Depth = depth;
        Unit = unit;
        DeclineBars = troughIndex - peakIndex;
        RecoveryBars = recoveryIndex - troughIndex;
        UnderwaterBars = recoveryIndex - peakIndex;
        DeclineDuration = declineDuration;
        RecoveryDuration = recoveryDuration;
        UnderwaterDuration = underwaterDuration;
    }
}

public sealed class DrawdownEpisodeResult
{
    public DrawdownEpisodeStatus Status { get; }
    public string? Reason { get; }
    public DrawdownEpisode? Episode { get; }

    internal DrawdownEpisodeResult(DrawdownEpisodeStatus status, string? reason, DrawdownEpisode? episode)
    {
        Status = status;
        Reason = reason;
        Episode = episode;
    }

    internal static DrawdownEpisodeResult Partial() =>
        new(DrawdownEpisodeStatus.NotComputedPartial, DrawdownReasonCodes.NotComputedPartial, null);
}
