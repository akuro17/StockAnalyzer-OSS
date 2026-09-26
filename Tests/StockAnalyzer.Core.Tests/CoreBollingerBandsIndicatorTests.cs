using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreBollingerBandsIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectBands_ViaResult()
    {
        var indicator = new CoreBollingerBandsIndicator { Period = 3, StdDevMultiplier = 2 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 14, 16, 18 });

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.UpperSeriesName));
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.LowerSeriesName));
        Assert.True(result.HasSeries(CoreBollingerBandsIndicator.MiddleSeriesName));

        var upper = result.GetSeries(CoreBollingerBandsIndicator.UpperSeriesName);
        var lower = result.GetSeries(CoreBollingerBandsIndicator.LowerSeriesName);
        var middle = result.GetSeries(CoreBollingerBandsIndicator.MiddleSeriesName);

        Assert.Equal(5, middle.Count);
        Assert.Equal(5, upper.Count);
        Assert.Equal(5, lower.Count);

        // Check values for index 2 (10, 12, 14 -> SMA 12)
        // StdDev: Sqrt(((10-12)^2 + (12-12)^2 + (14-12)^2) / 3) = Sqrt((4 + 0 + 4) / 3) = Sqrt(8/3) = Sqrt(2.666) ~= 1.633
        // Upper: 12 + 2 * 1.633 = 15.266
        // Lower: 12 - 2 * 1.633 = 8.734
        
        Assert.Equal(12m, middle[2]);
        Assert.NotNull(upper[2]);
        Assert.NotNull(lower[2]);
        
        // Use precision for float comparison
        Assert.Equal(15.266m, upper[2].Value, 3);
        Assert.Equal(8.734m, lower[2].Value, 3);
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmptyResult()
    {
        var indicator = new CoreBollingerBandsIndicator { Period = 5 };
        var result = indicator.Calculate(new List<CoreCandleData>());
        
        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }
}
