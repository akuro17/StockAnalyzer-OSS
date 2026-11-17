using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>Real daily bars (one per calendar day) for the tests of the bar-based drawing-value calculators; every value is a decimal built from the day number.</summary>
internal static class DrawingValueTestCandles
{
    /// <summary>The first bar of <see cref="Daily"/>.</summary>
    public static readonly DateTime First = new(2025, 11, 1);

    /// <summary>
    /// <paramref name="count"/> bars from <see cref="First"/>: close = 100 + day * 0.5, open = close - 0.25, high = close + 1, low = close - 1, volume = 1000 + 10 * day.
    /// The prices rise steadily, so the regression is exact and every period has a known high, low and close.
    /// </summary>
    public static IReadOnlyList<CoreCandleData> Daily(int count = 150)
    {
        var bars = new List<CoreCandleData>(count);
        for (int day = 0; day < count; day++)
        {
            decimal close = 100m + day * 0.5m;
            bars.Add(new CoreCandleData(First.AddDays(day), close - 0.25m, close + 1m, close - 1m, close, 1000 + 10L * day));
        }

        return bars;
    }
}
