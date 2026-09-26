using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class TimeAtPriceObjectTests
{
    private static List<CoreCandleData> MakeSampleCandles()
    {
        var list = new List<CoreCandleData>();
        var dates = new[]
        {
            new DateTime(2025, 1, 2),
            new DateTime(2025, 1, 5),
            new DateTime(2025, 1, 6),
            new DateTime(2025, 1, 7),
            new DateTime(2025, 1, 8)
        };

        for (int i = 0; i < dates.Length; i++)
        {
            decimal price = 50m + i * 5m;
            list.Add(new CoreCandleData(dates[i], price, price + 5m, price - 5m, price + 2m, 1000));
        }
        return list;
    }

    [Fact]
    public void TimeAtPriceObject_Defaults()
    {
        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 50m);
        var p2 = new ChartPoint(new DateTime(2025, 1, 5), 60m);
        using var obj = new TimeAtPriceObject(p1, p2);

        Assert.Equal(ChartObjectType.TimeAtPrice, obj.Type);
        Assert.Equal(PriceType.Close, obj.PriceType);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(Color.Parse("#FFEB3B"), obj.ValueAreaColor);
        Assert.Equal(Color.Parse("#90A4AE"), obj.ProfileColor);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.False(obj.LockRange);
        Assert.Equal(0, obj.RangeBars);
        Assert.Equal(VolumeProfileRepeatMode.Default, obj.RepeatMode);
        Assert.Empty(obj.Segments);
    }

    [Fact]
    public void TimeAtPriceObject_Recalculate_CalculatesProfileDataAndValues()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[4].Timestamp, 60m);
        using var obj = new TimeAtPriceObject(p1, p2);

        obj.Recalculate(candles);

        Assert.NotEmpty(obj.ProfileData);
        Assert.True(obj.VAH >= obj.VAL);

        var calcValues = obj.GetCalculatedValues(candles[0].Timestamp);
        Assert.Contains(calcValues, v => v.Key == "TPOC");
        Assert.Contains(calcValues, v => v.Key == "VAH");
        Assert.Contains(calcValues, v => v.Key == "VAL");
    }

    [Fact]
    public void TimeAtPriceObject_PriceType_AffectsDistribution()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[4].Timestamp, 60m);

        using var objClose = new TimeAtPriceObject(p1, p2) { PriceType = PriceType.Close };
        using var objLow = new TimeAtPriceObject(p1, p2) { PriceType = PriceType.Low };

        objClose.Recalculate(candles);
        objLow.Recalculate(candles);

        Assert.Equal(PriceType.Close, objClose.PriceType);
        Assert.Equal(PriceType.Low, objLow.PriceType);
    }

    [Fact]
    public void TimeAtPriceObject_AdjustRightPointToBars()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[0].Timestamp, 50m);
        var p2 = new ChartPoint(candles[1].Timestamp, 60m);
        using var obj = new TimeAtPriceObject(p1, p2);

        obj.AdjustRightPointToBars(4, candles);
        Assert.Equal(candles[0].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[3].Timestamp, obj.Points[1].Time);
    }

    [Fact]
    public void TimeAtPriceObject_TranslateBars()
    {
        var candles = MakeSampleCandles();
        var p1 = new ChartPoint(candles[1].Timestamp, 50m);
        var p2 = new ChartPoint(candles[2].Timestamp, 60m);
        using var obj = new TimeAtPriceObject(p1, p2);

        obj.TranslateBars(1, candles);
        Assert.Equal(candles[2].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[3].Timestamp, obj.Points[1].Time);
    }
}
