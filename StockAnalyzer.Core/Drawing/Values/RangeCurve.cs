namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>Why a range fit has no curve (or that it has).</summary>
public enum RangeFitStatus
{
    /// <summary>The fit succeeded.</summary>
    Ok,

    /// <summary>No bar lies between the two anchor times.</summary>
    NoRange,

    /// <summary>The range has fewer bars than the tool needs.</summary>
    TooFewBars,

    /// <summary>The kernel could not fit the model to the range (a price not above zero for a log model, equal prices, a singular system, a non-finite value).</summary>
    FitFailed,
}

/// <summary>The texts the range-fit tools share (the chart overlay and the Data-tab state row).</summary>
public static class RangeFitText
{
    /// <summary>The state text of a fit without a curve; empty for <see cref="RangeFitStatus.Ok"/>.</summary>
    public static string StatusText(RangeFitStatus status, int minBars) => status switch
    {
        RangeFitStatus.Ok => string.Empty,
        RangeFitStatus.NoRange => "No bars in the range",
        RangeFitStatus.TooFewBars => string.Create(DrawingValueFormat.Culture, $"Insufficient bars (< {minBars})"),
        _ => "Fit failed",
    };

    /// <summary>The badge of a fit that used the newest bars only ("Last N bars"); empty when the whole range was used.</summary>
    public static string TruncationBadge(int rangeBars, int effectiveBars)
        => effectiveBars > 0 && effectiveBars < rangeBars ? string.Create(DrawingValueFormat.Culture, $"Last {effectiveBars} bars") : string.Empty;
}

/// <summary>
/// The centre curve a range-fit tool draws: one point per fitted bar (<see cref="FittedCount"/> of them, at the bar times of the fit window), then the projected bars after the range end
/// (existing bars first, then bars one bar span apart), the price of the curve at each, and the half width of the optional band around it (0 = no band).
/// </summary>
public sealed class RangeCurve
{
    /// <summary>A curve without a point.</summary>
    public static readonly RangeCurve Empty = new(Array.Empty<DateTime>(), Array.Empty<decimal>(), 0, 0m);

    public RangeCurve(DateTime[] times, decimal[] prices, int fittedCount, decimal bandHalfWidth)
    {
        Times = times;
        Prices = prices;
        FittedCount = fittedCount;
        BandHalfWidth = bandHalfWidth;
    }

    /// <summary>The time of every point (fitted bars, then projected bars).</summary>
    public DateTime[] Times { get; }

    /// <summary>The curve price of every point.</summary>
    public decimal[] Prices { get; }

    /// <summary>The number of leading points that lie on fitted bars (the rest is projection).</summary>
    public int FittedCount { get; }

    /// <summary>The half width of the band (price units, above and below the curve); 0 when the tool has no band.</summary>
    public decimal BandHalfWidth { get; }

    /// <summary>The number of points.</summary>
    public int Count => Prices.Length;

    /// <summary>
    /// The times of a curve over the newest <paramref name="fittedCount"/> bars up to <paramref name="lastIndex"/> followed by <paramref name="extraBars"/> projected bars: the time of an existing
    /// bar when there is one, otherwise the newest bar time plus the mean bar span for every bar beyond the data (<see cref="DrawingRangeBars.FutureBarTime"/>).
    /// </summary>
    public static DateTime[] BuildTimes(IReadOnlyList<CoreCandleData> candles, int lastIndex, int fittedCount, int extraBars)
    {
        var times = new DateTime[fittedCount + extraBars];
        int firstFitIndex = lastIndex - fittedCount + 1;
        for (int i = 0; i < fittedCount; i++)
        {
            times[i] = candles[firstFitIndex + i].Timestamp;
        }

        if (extraBars > 0)
        {
            TimeSpan span = DrawingRangeBars.MeanBarSpan(candles);
            for (int k = 1; k <= extraBars; k++)
            {
                times[fittedCount + k - 1] = DrawingRangeBars.FutureBarTime(candles, lastIndex, k, span);
            }
        }

        return times;
    }

    /// <summary>
    /// A curve over the first <paramref name="validCount"/> points of <paramref name="times"/> / <paramref name="prices"/> (the projection is cut where a value was not finite;
    /// the fitted part must be complete).
    /// </summary>
    public static RangeCurve Create(DateTime[] times, decimal[] prices, int validCount, int fittedCount, decimal bandHalfWidth)
    {
        if (validCount == times.Length)
        {
            return new RangeCurve(times, prices, fittedCount, bandHalfWidth);
        }

        var cutTimes = new DateTime[validCount];
        var cutPrices = new decimal[validCount];
        Array.Copy(times, cutTimes, validCount);
        Array.Copy(prices, cutPrices, validCount);
        return new RangeCurve(cutTimes, cutPrices, fittedCount, bandHalfWidth);
    }
}
