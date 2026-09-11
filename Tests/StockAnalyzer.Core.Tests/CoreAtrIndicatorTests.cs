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

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreAtrIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
