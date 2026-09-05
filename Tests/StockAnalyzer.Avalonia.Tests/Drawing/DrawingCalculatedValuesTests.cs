using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingCalculatedValuesTests
{
    [Fact]
    public void LongShortPositionObject_GetCalculatedValues_ReturnsAccurateMetrics()
    {
        var dt = new DateTime(2026, 1, 1);
        var entry = new ChartPoint(dt, 100m);
        var stop = new ChartPoint(dt, 90m);
        var target = new ChartPoint(dt, 120m);
        var obj = new LongShortPositionObject(entry, stop, target, isLong: true);

        var values = obj.GetCalculatedValues(dt);

        Assert.Equal(4, values.Count);
        Assert.Equal("Entry", values[0].Key);
        Assert.Equal(100m, values[0].NumericValue);
        Assert.Equal("100.00", values[0].FormattedText);

        Assert.Equal("Target", values[1].Key);
        Assert.Equal(120m, values[1].NumericValue);
        Assert.Contains("+20.00", values[1].FormattedText);

        Assert.Equal("Stop", values[2].Key);
        Assert.Equal(90m, values[2].NumericValue);
        Assert.Contains("-10.00", values[2].FormattedText);

        Assert.Equal("RiskReward", values[3].Key);
        Assert.Equal(2.0m, values[3].NumericValue);
        Assert.Equal("1 : 2.00", values[3].FormattedText);
    }

    [Fact]
    public void HorizontalLineObject_GetCalculatedValues_ReturnsPrice()
    {
        var dt = new DateTime(2026, 1, 1);
        var obj = new HorizontalLineObject(new ChartPoint(dt, 150.5m));

        var values = obj.GetCalculatedValues(dt);

        Assert.Single(values);
        Assert.Equal("Price", values[0].Key);
        Assert.Equal(150.5m, values[0].NumericValue);
        Assert.Equal("150.500", values[0].FormattedText);
    }

    [Fact]
    public void TrendLineObject_GetCalculatedValues_InterpolatesCorrectPrice()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0);
        var t2 = new DateTime(2026, 1, 3, 0, 0, 0);
        var midTime = new DateTime(2026, 1, 2, 0, 0, 0); // 50% between t1 and t2

        var obj = new TrendLineObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m));

        var values = obj.GetCalculatedValues(midTime);

        Assert.Equal(3, values.Count);
        Assert.Equal("TrendLinePrice", values[0].Key);
        Assert.Equal(150m, values[0].NumericValue);
        Assert.Equal("150.000", values[0].FormattedText);
    }

    [Fact]
    public void RegressionTrendObject_GetCalculatedValues_ReturnsSlopeAndBands()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 5);

        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 1, 1), 10m, 10m, 10m, 10m, 100),
            new(new DateTime(2026, 1, 2), 20m, 20m, 20m, 20m, 100),
            new(new DateTime(2026, 1, 3), 30m, 30m, 30m, 30m, 100),
            new(new DateTime(2026, 1, 4), 40m, 40m, 40m, 40m, 100),
            new(new DateTime(2026, 1, 5), 50m, 50m, 50m, 50m, 100),
        };

        var obj = new RegressionTrendObject(new ChartPoint(t1, 10m), new ChartPoint(t2, 50m));
        obj.Recalculate(candles);

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 3));

        Assert.Equal(5, values.Count);
        Assert.Equal("RegressionCenter", values[0].Key);
        Assert.Equal(30m, values[0].NumericValue);
        Assert.Equal("Slope", values[3].Key);
        Assert.Equal(10m, values[3].NumericValue);
    }

    [Fact]
    public void AnchoredVwapObject_GetCalculatedValues_ReturnsAnchorAndVwapPrice()
    {
        var t1 = new DateTime(2026, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 1, 1), 100m, 100m, 100m, 100m, 1000),
            new(new DateTime(2026, 1, 2), 110m, 110m, 110m, 110m, 1000),
        };

        var obj = new AnchoredVwapObject(new ChartPoint(t1, 100m));
        obj.Recalculate(candles);

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 2));

        Assert.Equal(2, values.Count);
        Assert.Equal("AnchorDate", values[0].Key);
        Assert.Equal("2026/01/01", values[0].FormattedText);
        Assert.Equal("AnchoredVWAP", values[1].Key);
        Assert.Equal(105m, values[1].NumericValue); // (100*1000 + 110*1000) / 2000 = 105
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_GetCalculatedValues_ReturnsPocVahVal()
    {
        var t1 = new DateTime(2026, 1, 1);
        var t2 = new DateTime(2026, 1, 2);

        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 1, 1), 100m, 105m, 95m, 100m, 1000),
            new(new DateTime(2026, 1, 2), 100m, 105m, 95m, 100m, 2000),
        };

        var obj = new FixedRangeVolumeProfileObject(new ChartPoint(t1, 95m), new ChartPoint(t2, 105m));
        obj.Recalculate(candles);

        var values = obj.GetCalculatedValues(t2);

        Assert.Equal(3, values.Count);
        Assert.Equal("POC", values[0].Key);
        Assert.Equal("VAH", values[1].Key);
        Assert.Equal("VAL", values[2].Key);
    }
}
