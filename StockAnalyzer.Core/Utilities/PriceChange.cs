using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// The change of one bar from the previous bar's close. <see cref="Amount"/> is price-native (the ticker's currency),
/// <see cref="Percent"/> is a percentage (5 means +5%).
/// </summary>
public readonly record struct PriceChange(decimal Amount, decimal Percent)
{
    /// <summary>No change, also the value when the previous bar does not exist.</summary>
    public static readonly PriceChange None = new(0m, 0m);

    /// <summary>
    /// The single definition of "Change" and "Change %" of a bar measured from the previous close:
    /// <c>Amount = close - previousClose</c> and <c>Percent = Amount / previousClose * 100</c>. The open price is not used.
    /// When <paramref name="previousClose"/> is 0 the percentage is 0 and the amount is the close itself. A negative previous close (crude oil futures
    /// traded below 0 in 2020) is divided as it is, so the percentage of a rise from -40 to -20 is -50 (owner decision 2026-10-14).
    /// </summary>
    public static PriceChange FromCloses(decimal close, decimal previousClose)
    {
        decimal amount = close - previousClose;
        decimal percent = previousClose != 0m ? amount / previousClose * ChartConstants.PercentageMultiplier : 0m;
        return new PriceChange(amount, percent);
    }

    /// <summary>
    /// <see cref="FromCloses"/> for the bar at <paramref name="index"/> of <paramref name="ascendingCandles"/>, which must be in chronological order
    /// (oldest first). Without a previous bar (null or empty list, <paramref name="index"/> not in 1..Count-1) the result is <see cref="None"/>.
    /// Never throws, does not change its input and does not allocate.
    /// </summary>
    public static PriceChange FromPreviousClose(IReadOnlyList<CandleData>? ascendingCandles, int index)
    {
        if (ascendingCandles == null || index < 1 || index >= ascendingCandles.Count)
        {
            return None;
        }

        return FromCloses(ascendingCandles[index].Close, ascendingCandles[index - 1].Close);
    }

    /// <summary><see cref="FromPreviousClose(IReadOnlyList{CandleData}?, int)"/> for a list of <see cref="CoreCandleData"/>.</summary>
    public static PriceChange FromPreviousClose(IReadOnlyList<CoreCandleData>? ascendingCandles, int index)
    {
        if (ascendingCandles == null || index < 1 || index >= ascendingCandles.Count)
        {
            return None;
        }

        return FromCloses(ascendingCandles[index].Close, ascendingCandles[index - 1].Close);
    }
}
