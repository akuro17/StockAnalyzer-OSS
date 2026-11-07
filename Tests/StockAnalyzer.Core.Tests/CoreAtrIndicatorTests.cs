using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreAtrIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<(decimal open, decimal high, decimal low, decimal close)> values)
    {
        var startDate = DateTime.Today;
        return values.Select((v, i) => new CoreCandleData(
            startDate.AddDays(i), v.open, v.high, v.low, v.close, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        var indicator = new CoreAtrIndicator { Period = 3 };
        var candles = CreateTestCandles(new[]
        {
            (10m, 12m, 9m, 11m), // TR = 3
            (11m, 13m, 10m, 12m),// TR = Max(3, 2, 1) = 3
            (12m, 14m, 11m, 13m),// TR = Max(3, 2, 1) = 3
            (13m, 15m, 12m, 14m) // TR = Max(3, 2, 1) = 3
        });

        indicator.Calculate(candles);

        Assert.Equal(4, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        // At index 3: First ATR is SMA of TRs. TRs are all 3. So ATR = 3.
        // Note: The implementation calculates TR from index 1.
        // TR1=3, TR2=3, TR3=3. Average at candle index 3 (i=3) for period 3 is avg of TRs at i=1,2,3 which is 3.
        Assert.Equal(3m, indicator.Values[3]);
    }


    /// <summary>The ATR as it was written before the running sum (T9): a list of every true range and <c>Average()</c> for the first value. Kept here as the oracle.</summary>
    private static List<decimal?> OracleAtr(IReadOnlyList<CoreCandleData> candles, int period)
    {
        var values = new List<decimal?>();
        if (candles.Count < 2) return values;
        values.Add(null);

        var trList = new List<decimal>();
        for (int i = 1; i < candles.Count; i++)
        {
            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                                  Math.Abs(candles[i].Low - candles[i - 1].Close)));
            trList.Add(tr);

            if (i < period) { values.Add(null); continue; }

            if (i == period)
                values.Add(trList.Average());
            else
                values.Add(((values[^1]!.Value * (period - 1)) + tr) / period);
        }

        return values;
    }

    private static List<CoreCandleData> RandomCandles(int count, int seed)
    {
        var random = new Random(seed);
        decimal close = 100m;
        var rows = new List<(decimal open, decimal high, decimal low, decimal close)>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = close;
            // two decimals and up to six decimals alternate, so the sums carry fractional digits that a different summation order would change
            int scale = i % 2 == 0 ? 2 : 6;
            close = Math.Max(1m, Math.Round(open + ((decimal)(random.NextDouble() - 0.5) * 4m), scale));
            decimal high = Math.Round(Math.Max(open, close) + (decimal)(random.NextDouble() * 2), scale);
            decimal low = Math.Max(0.01m, Math.Round(Math.Min(open, close) - (decimal)(random.NextDouble() * 2), scale));
            rows.Add((open, high, low, close));
        }

        return CreateTestCandles(rows);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(14)]
    [InlineData(50)]
    [InlineData(200)]
    public void Calculate_EqualsTheListAndAverageOracle_OnRandomSeries(int period)
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            List<CoreCandleData> candles = RandomCandles(10_000, seed);
            var indicator = new CoreAtrIndicator { Period = period };

            indicator.Calculate(candles);

            Assert.Equal(OracleAtr(candles, period), indicator.Values.ToList());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Calculate_EqualsTheOracle_WhenThePeriodIsAtLeastTheSeriesLength(int bars)
    {
        // Period 5 on 0..5 bars: the first ATR value exists only from bar 5 on; shorter series stay all null.
        List<CoreCandleData> candles = RandomCandles(bars, 7);
        var indicator = new CoreAtrIndicator { Period = 5 };

        indicator.Calculate(candles);

        Assert.Equal(OracleAtr(candles, 5), indicator.Values.ToList());
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAtrIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
