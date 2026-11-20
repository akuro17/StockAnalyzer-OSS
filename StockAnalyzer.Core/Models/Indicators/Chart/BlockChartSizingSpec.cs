using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// How a block chart overlay resolves its box / brick / reversal size. The inputs and the arithmetic are those of the existing chart types (<see cref="AutoBoxSizeCalculator"/>); what
/// differs is WHEN the size is taken: once, at the anchor bar (see <see cref="BlockChartOverlayMapper"/>), so that the values of past bars never change.
/// In Auto ATR and Percentage modes the box size is fixed once, at the anchor bar (the first valid bar for which a positive size can be resolved: the bar after the ATR period for Auto ATR,
/// the first bar for Percentage), so that values of past bars never change. It can therefore differ from the box size of the corresponding non-time-series chart, which is derived from the whole series.
/// </summary>
public readonly record struct BlockChartSizingSpec(
    ChartSizingMode Mode,
    decimal ManualSize,
    decimal Percentage,
    int AtrPeriod,
    decimal AtrMultiplier,
    ChartRoundingMode Rounding,
    AutoFallbackMode Fallback)
{
    /// <summary>
    /// The first index (among the valid bars) at which a size can be tried: an ATR needs <see cref="AtrPeriod"/> + 1 bars, so index <see cref="AtrPeriod"/>; the other modes need one bar.
    /// </summary>
    public int FirstAnchorCandidate => Mode == ChartSizingMode.AutoAtr ? Math.Max(0, AtrPeriod) : 0;

    /// <summary>
    /// Resolves the size from <paramref name="prefix"/> (the valid bars up to and including the candidate anchor bar; the price used is the close of its last bar). False when the size after
    /// the rounding post-processing is not positive, so the caller tries the next bar. An ATR of exactly 0 is NOT a failure (the existing fallback size is positive); only a size that the
    /// rounding crushes to 0 is.
    /// </summary>
    public bool TryResolve(ReadOnlySpan<CoreCandleData> prefix, out decimal size)
    {
        if (prefix.IsEmpty)
        {
            size = 0m;
            return false;
        }

        return AutoBoxSizeCalculator.TryCalculateStrict(
            Mode, prefix, ManualSize, AtrPeriod, AtrMultiplier, Rounding, Fallback, prefix[^1].Close, Percentage, out size);
    }
}
