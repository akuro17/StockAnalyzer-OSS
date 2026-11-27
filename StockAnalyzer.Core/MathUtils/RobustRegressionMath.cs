using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// A robust straight line <c>y = Intercept + Slope * i</c> fitted to a window (<c>i</c> = 0-based offset in the window, <c>0</c> = the oldest bar).
/// <see cref="FittedLast"/> is the line at the newest bar (<c>W - 1</c>); <see cref="ResidualSigma"/> is <c>MadToSigma * median(|e_i - median(e)|)</c> of the residuals <c>e_i = y_i - Intercept - Slope * i</c>.
/// All values are in the unit of the input (price per bar for <see cref="Slope"/>).
/// </summary>
public readonly record struct RobustLine(decimal Slope, decimal Intercept, decimal FittedLast, decimal ResidualSigma);

/// <summary>
/// Theil-Sen and Siegel repeated-median straight-line fits of a window of <see cref="decimal"/> prices (owner decision U-2 = A: price-native statistics lose no precision).
/// <list type="bullet">
/// <item>Theil-Sen: the slope is the median (type 7, so the mean of the two middle values for an even count) of all <c>W (W - 1) / 2</c> pair slopes <c>(y_j - y_i) / (j - i)</c>, <c>i &lt; j</c>.</item>
/// <item>Siegel: <c>m_i = median_{j != i}</c> of the slopes through bar <c>i</c>, slope <c>m = median_i(m_i)</c>.</item>
/// <item>Both: <c>Intercept = median_i(y_i - m i)</c> (the line value at window offset 0, owner decision U-13 = A), <c>FittedLast = Intercept + m (W - 1)</c>.</item>
/// </list>
/// Cost O(W^2) per window: this is why every caller has a measured upper bound for the window (plan task T7): <see cref="MaxWindowLength"/> (the <see cref="TryFit(ReadOnlySpan{decimal}, bool, Span{decimal}, out RobustLine)"/> default, the MCP <c>trendBars</c>, the DSL functions, the flag rails), <c>IndicatorDefaultConstants.TheilSenMaxPeriod</c> (the indicator, every bar of a series is fitted) and <see cref="HardMaxWindowLength"/> (the overload that takes its own limit, one window at a time).
/// Specification: docs/indicators_formulas.md, records indicators_formulas_0594 (subject RobustRegressionMath) and indicators_formulas_0604 (Theil-Sen regression).
/// </summary>
public static class RobustRegressionMath
{
    /// <summary>Shortest window of a line: two points.</summary>
    public const int MinWindowLength = 2;

    /// <summary>
    /// The window limit of <see cref="TryFit(ReadOnlySpan{decimal}, bool, Span{decimal}, out RobustLine)"/> and <see cref="TryTheilSenSlope"/>: the <c>trendBars</c> of the MCP <c>robustTrendSlope</c>
    /// (<c>SeriesSummaryStatistics.MaxRobustTrendBars</c>), the DSL robust functions and the flag rails take this one value. The TheilSenRegression indicator has its own larger bound
    /// (<c>IndicatorDefaultConstants.TheilSenMaxPeriod</c>, 2026-11-27) and passes it to the overload. The pair-slope cost is quadratic; 5,000 bars of a window of 30 calculate in 318 ms (Theil-Sen) and
    /// 382 ms (Siegel) in Release and in 334 / 675 ms in Debug, while 40 already needs 1,097 ms in Debug (Siegel). Owner decision 2026-11-17 (no candidate of the plan's {250, 120, 60} met the
    /// 1,000 ms rule in both modes; probe table in the step log of the plan).
    /// </summary>
    public const int MaxWindowLength = 30;

    /// <summary>
    /// The hard cap of the overload of <see cref="TryFit(ReadOnlySpan{decimal}, bool, Span{decimal}, int, out RobustLine)"/> that takes its own largest window: a call that asks for more is a programming error.
    /// It is for a caller that fits ONE window at a time (the Robust Regression Trend drawing tool); callers that fit every bar of a series keep <see cref="MaxWindowLength"/>.
    /// Measured 2026-11-27 (one fit, Debug / Release, median of runs): a window of 500 takes 16 / 9 ms (Theil-Sen) and 30 / 19 ms (Siegel) and needs 1.9 MB of scratch memory (Theil-Sen; Siegel 31 KB),
    /// 1,000 takes 53 / 35 ms and 96 / 67 ms with 7.7 MB; the time stays under the 100 ms rule up to 1,000 but the quadratic scratch of Theil-Sen reaches the large object heap at 500 and is 8 MB at 1,000,
    /// so the owner limit is 500 (probe table in the step log of the drawing tool follow-up).
    /// </summary>
    public const int HardMaxWindowLength = 500;

    /// <summary>Number of pair slopes of a window, <c>W (W - 1) / 2</c>.</summary>
    public static int PairCount(int windowLength) => (int)((long)windowLength * (windowLength - 1) / 2);

    /// <summary>Elements of <see cref="decimal"/> scratch memory <see cref="TryFit"/> needs for a window of <paramref name="windowLength"/> values.</summary>
    public static int ScratchLength(int windowLength, bool siegel) =>
        (siegel ? (2 * windowLength) - 1 : PairCount(windowLength)) + (2 * windowLength);

    /// <summary>
    /// Fits the line. False when the window is shorter than <see cref="MinWindowLength"/>, longer than <see cref="MaxWindowLength"/>, <paramref name="scratch"/> is shorter than
    /// <see cref="ScratchLength"/>, or a decimal operation overflows. Allocates nothing.
    /// </summary>
    public static bool TryFit(ReadOnlySpan<decimal> window, bool siegel, Span<decimal> scratch, out RobustLine line)
        => TryFitCore(window, siegel, scratch, MaxWindowLength, out line);

    /// <summary>
    /// <see cref="TryFit(ReadOnlySpan{decimal}, bool, Span{decimal}, out RobustLine)"/> with its own largest window. Fits the same line by the same code; only the limit differs: False when the window is longer than
    /// <paramref name="maxWindowLength"/> (data dependent, like the other refusals). <paramref name="scratch"/> may be longer than <see cref="ScratchLength"/> and its content is irrelevant (every element is written before it is read).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWindowLength"/> is below <see cref="MinWindowLength"/> or above <see cref="HardMaxWindowLength"/> (a call error, so no scratch of that size is ever asked for).</exception>
    public static bool TryFit(ReadOnlySpan<decimal> window, bool siegel, Span<decimal> scratch, int maxWindowLength, out RobustLine line)
    {
        if (maxWindowLength < MinWindowLength || maxWindowLength > HardMaxWindowLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWindowLength), maxWindowLength, $"The largest window must be {MinWindowLength}..{HardMaxWindowLength}.");
        }

        return TryFitCore(window, siegel, scratch, maxWindowLength, out line);
    }

    private static bool TryFitCore(ReadOnlySpan<decimal> window, bool siegel, Span<decimal> scratch, int maxWindowLength, out RobustLine line)
    {
        line = default;
        int w = window.Length;
        if (w < MinWindowLength || w > maxWindowLength || scratch.Length < ScratchLength(w, siegel))
        {
            return false;
        }

        try
        {
            decimal slope;
            int slopeBufferLength = siegel ? (2 * w) - 1 : PairCount(w);
            if (siegel)
            {
                Span<decimal> row = scratch.Slice(0, w - 1);
                Span<decimal> perBar = scratch.Slice(w - 1, w);
                for (int i = 0; i < w; i++)
                {
                    int count = 0;
                    for (int j = 0; j < w; j++)
                    {
                        if (j != i)
                        {
                            row[count++] = PairSlope(window, i, j);
                        }
                    }

                    perBar[i] = OrderStatisticsMath.Median(row) ?? throw new OverflowException();
                }

                slope = OrderStatisticsMath.Median(perBar) ?? throw new OverflowException();
            }
            else
            {
                Span<decimal> slopes = scratch.Slice(0, slopeBufferLength);
                int count = 0;
                for (int i = 0; i < w - 1; i++)
                {
                    for (int j = i + 1; j < w; j++)
                    {
                        slopes[count++] = (window[j] - window[i]) / (j - i);
                    }
                }

                slope = OrderStatisticsMath.Median(slopes) ?? throw new OverflowException();
            }

            Span<decimal> work = scratch.Slice(slopeBufferLength, w);
            Span<decimal> madScratch = scratch.Slice(slopeBufferLength + w, w);
            for (int i = 0; i < w; i++)
            {
                work[i] = window[i] - (slope * i);
            }

            decimal intercept = OrderStatisticsMath.Median(work) ?? throw new OverflowException();
            for (int i = 0; i < w; i++)
            {
                work[i] = window[i] - (intercept + (slope * i));
            }

            decimal mad = OrderStatisticsMath.MedianAbsoluteDeviation(work, madScratch) ?? throw new OverflowException();
            line = new RobustLine(slope, intercept, intercept + (slope * (w - 1)), OrderStatisticsMath.MadToSigmaDecimal * mad);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Theil-Sen slope of points at strictly increasing integer positions <paramref name="offsets"/> (a window with gaps: a missing bar leaves a gap on the position axis):
    /// the type 7 median of the pair slopes <c>(v_j - v_i) / (x_j - x_i)</c>. For positions 0, 1, 2, ... this is the slope <see cref="TryFit"/> finds. Used by the MCP series summary
    /// (<c>robustTrendSlope</c>). False when fewer than <see cref="MinWindowLength"/> or more than <see cref="MaxWindowLength"/> points, the lengths differ, the positions do not increase,
    /// <paramref name="scratch"/> holds fewer than <see cref="PairCount"/> elements, or a decimal operation overflows. Allocates nothing.
    /// </summary>
    public static bool TryTheilSenSlope(ReadOnlySpan<int> offsets, ReadOnlySpan<decimal> values, Span<decimal> scratch, out decimal slope)
    {
        slope = default;
        int n = values.Length;
        if (offsets.Length != n || n < MinWindowLength || n > MaxWindowLength || scratch.Length < PairCount(n))
        {
            return false;
        }

        try
        {
            int count = 0;
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (offsets[j] <= offsets[i])
                    {
                        return false;
                    }

                    scratch[count++] = (values[j] - values[i]) / (offsets[j] - offsets[i]);
                }
            }

            if (OrderStatisticsMath.Median(scratch.Slice(0, count)) is not { } median)
            {
                return false;
            }

            slope = median;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal PairSlope(ReadOnlySpan<decimal> window, int i, int j) =>
        i < j ? (window[j] - window[i]) / (j - i) : (window[i] - window[j]) / (i - j);
}
