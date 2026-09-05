using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Tests.Drawing;

public class LogScaleAutoscaleTests
{
    [Fact]
    public void LogScale_HighVolatility_CalculatesPositivePaddedMinPrice()
    {
        // Arrange: High volatility candles (from 100 to 10,000)
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(DateTime.Now.AddDays(-10), 100m, 120m, 100m, 110m, 1000),
            new CoreCandleData(DateTime.Now.AddDays(-5), 500m, 600m, 450m, 550m, 2000),
            new CoreCandleData(DateTime.Now, 9500m, 10000m, 9000m, 9800m, 5000)
        };

        // Act: Create snapshot with Log scale and 20px padding (height 500px)
        var snapshot = new ChartDataSnapshot(
            candles,
            symbol: "285A-T",
            paddingTopPx: 20m,
            paddingBottomPx: 20m,
            chartHeightPx: 500m,
            priceScale: PriceScaleType.Log);

        // Assert: MinPrice must be strictly positive and > 50 (not negative -296 or tiny 0.0001)
        Assert.True(snapshot.MinPrice > 50m, $"MinPrice should be reasonably padded > 50m, but was {snapshot.MinPrice}");
        Assert.True(snapshot.MaxPrice > 10000m, $"MaxPrice should be > 10000m, but was {snapshot.MaxPrice}");
        Assert.Equal(PriceScaleType.Log, snapshot.PriceScale);
    }

    [Fact]
    public void LogScale_GenericCoordinateTransform_MapsPricesEvenly()
    {
        // Arrange: GenericCoordinateTransform configured for Log Scale with 100 to 10,000 range
        var transform = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        transform.PriceScale = PriceScaleType.Log;
        transform.UpdateRange(DateTime.Now.AddDays(-10), DateTime.Now, 80m, 12000m);

        // Act: Map prices 100 and 10,000 to screen Y
        double yLow = transform.GetYFromPrice(100m);
        double yHigh = transform.GetYFromPrice(10000m);

        // Assert: High price should be near top (small Y), Low price near bottom (larger Y)
        Assert.True(yHigh < yLow, "Higher price should have smaller screen Y coordinate");
        Assert.True(yLow <= 500, $"Low price Y ({yLow}) should be within canvas height");
        Assert.True(yHigh >= 0, $"High price Y ({yHigh}) should be non-negative");
    }
}
