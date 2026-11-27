using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// The selected price of every bar of a drawing range (the single definition for the range tools that offer a <see cref="PriceType"/>: the GARCH cone and the Robust Regression Trend).
/// The price walk itself (the initial state of a range, True High / True Low and Heikin-Ashi) is <see cref="RangePriceWalker"/>, shared with the distribution range: the bars BEFORE the first bar of the range are never read.
/// A price that is not above zero is returned as it is: the caller decides what that means for its calculation (the GARCH cone reports it, see <c>GarchConeStatus.NonPositivePrice</c>).
/// </summary>
public static class DrawingRangePriceSeries
{
    /// <summary>
    /// The price of the bars <paramref name="firstIndex"/> .. <paramref name="lastIndex"/> (both included, oldest first); an empty array for an invalid range.
    /// <see cref="PriceType.Close"/> is the closes themselves.
    /// </summary>
    public static decimal[] Extract(IReadOnlyList<CoreCandleData> candles, int firstIndex, int lastIndex, PriceType priceSource)
    {
        if (candles == null || firstIndex < 0 || lastIndex >= candles.Count || firstIndex > lastIndex)
        {
            return Array.Empty<decimal>();
        }

        var prices = new decimal[lastIndex - firstIndex + 1];
        var walker = new RangePriceWalker(priceSource);
        for (int i = 0; i < prices.Length; i++)
        {
            CoreCandleData candle = candles[firstIndex + i];
            prices[i] = walker.Next(in candle);
        }

        return prices;
    }
}
