using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CorePriceIndicatorTests
{
    [Fact]
    public void Properties_HaveCorrectDefaults()
    {
        var indicator = new CorePriceIndicator();
        Assert.Equal("Price", indicator.Name);
        Assert.True(indicator.IsOverlay);
        Assert.Equal(PriceType.Close, indicator.PriceSource);
    }

    [Fact]
    public void Calculate_ExtractsExpectedPriceSeries()
    {
        var candles = new List<CoreCandleData>
        {
            new(DateTime.Today, 100m, 120m, 80m, 110m, 1000),
            new(DateTime.Today.AddDays(1), 110m, 130m, 90m, 120m, 1000),
        };

        // Close
        var indicator = new CorePriceIndicator { PriceSource = PriceType.Close };
        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful);
        Assert.Equal(2, result.MainValues.Count);
        Assert.Equal(110m, result.MainValues[0]);
        Assert.Equal(120m, result.MainValues[1]);

        // Median (High + Low) / 2
        indicator.PriceSource = PriceType.Median;
        result = indicator.Calculate(candles);
        Assert.Equal(100m, result.MainValues[0]); // (120+80)/2
        Assert.Equal(110m, result.MainValues[1]); // (130+90)/2

        // Heikin-Ashi Close
        indicator.PriceSource = PriceType.HeikinAshiClose;
        result = indicator.Calculate(candles);
        Assert.Equal(102.5m, result.MainValues[0]); // (100+120+80+110)/4
        Assert.Equal(112.5m, result.MainValues[1]); // (110+130+90+120)/4
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CorePriceIndicator();
        var result = indicator.Calculate(new List<CoreCandleData>());
        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }
}
