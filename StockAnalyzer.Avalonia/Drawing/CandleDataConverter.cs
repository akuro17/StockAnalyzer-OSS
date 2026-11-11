using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// SSoT for converting <see cref="CoreCandleData"/> (record class) to the older <see cref="CandleData"/> (record struct) that
/// <c>HarmonicPatternObject</c>, <c>AutoElliottWaveObject</c> and <c>GeometricPatternObject</c>'s <c>Recalculate</c> still take.
/// The mapping is field by field and keeps every value as it is (decimal prices, same order).
/// </summary>
internal static class CandleDataConverter
{
    /// <summary>Lazy conversion: nothing is converted until the sequence is enumerated, and it is converted again on each enumeration.</summary>
    public static IEnumerable<CandleData> ToLazy(IReadOnlyList<CoreCandleData> candles)
        => candles.Select(Convert);

    /// <summary>Eager conversion into a new array, in the order of <paramref name="candles"/>.</summary>
    public static CandleData[] ToArray(IReadOnlyList<CoreCandleData> candles)
    {
        var result = new CandleData[candles.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert(candles[i]);
        }

        return result;
    }

    private static CandleData Convert(CoreCandleData c)
        => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume);
}
