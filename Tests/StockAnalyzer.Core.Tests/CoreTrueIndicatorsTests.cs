using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class CoreTrueIndicatorsTests
{
    private static List<CoreCandleData> CreateSampleCandles()
    {
        var baseDate = new DateTime(2026, 1, 1);
        return new List<CoreCandleData>
        {
            // Bar 0: O=100, H=105, L=95, C=102
            new(baseDate, 100m, 105m, 95m, 102m, 1000),
            // Bar 1 (normal uptrend): O=103, H=108, L=101, C=107 (prevClose=102)
            // TH = max(108, 102) = 108, TL = min(101, 102) = 101, TR = 108 - 101 = 7
            new(baseDate.AddDays(1), 103m, 108m, 101m, 107m, 1200),
            // Bar 2 (gap down): O=90, H=98, L=85, C=92 (prevClose=107)
            // TH = max(98, 107) = 107, TL = min(85, 107) = 85, TR = 107 - 85 = 22
            new(baseDate.AddDays(2), 90m, 98m, 85m, 92m, 1500),
            // Bar 3 (gap up): O=115, H=125, L=112, C=120 (prevClose=92)
            // TH = max(125, 92) = 125, TL = min(112, 92) = 92, TR = 125 - 92 = 33
            new(baseDate.AddDays(3), 115m, 125m, 112m, 120m, 1800)
        };
    }

    [Fact]
    public void TrueHigh_CalculatesCorrectly()
    {
        var indicator = new CoreTrueHighIndicator();
        var candles = CreateSampleCandles();

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(indicator.IsOverlay);
        Assert.Equal(4, indicator.Values.Count);
        Assert.Equal(105m, indicator.Values[0]);
        Assert.Equal(108m, indicator.Values[1]);
        Assert.Equal(107m, indicator.Values[2]); // Previous close (107) > Today's high (98)
        Assert.Equal(125m, indicator.Values[3]); // Today's high (125) > Previous close (92)
    }

    [Fact]
    public void TrueLow_CalculatesCorrectly()
    {
        var indicator = new CoreTrueLowIndicator();
        var candles = CreateSampleCandles();

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(indicator.IsOverlay);
        Assert.Equal(4, indicator.Values.Count);
        Assert.Equal(95m, indicator.Values[0]);
        Assert.Equal(101m, indicator.Values[1]); // Today's low (101) < Previous close (102)
        Assert.Equal(85m, indicator.Values[2]);  // Today's low (85) < Previous close (107)
        Assert.Equal(92m, indicator.Values[3]);  // Previous close (92) < Today's low (112)
    }

    [Fact]
    public void TrueRange_CalculatesCorrectly()
    {
        var indicator = new CoreTrueRangeIndicator();
        var candles = CreateSampleCandles();

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.False(indicator.IsOverlay);
        Assert.Equal(4, indicator.Values.Count);
        Assert.Equal(10m, indicator.Values[0]); // 105 - 95 = 10
        Assert.Equal(7m, indicator.Values[1]);  // 108 - 101 = 7
        Assert.Equal(22m, indicator.Values[2]); // 107 - 85 = 22
        Assert.Equal(33m, indicator.Values[3]); // 125 - 92 = 33
    }

    [Fact]
    public void TrueIndicators_HandleEmptyAndNullCandlesGracefully()
    {
        var th = new CoreTrueHighIndicator();
        var tl = new CoreTrueLowIndicator();
        var tr = new CoreTrueRangeIndicator();

        var emptyList = new List<CoreCandleData>();

        Assert.True(th.Calculate(emptyList).IsSuccessful);
        Assert.Empty(th.Values);

        Assert.True(tl.Calculate(emptyList).IsSuccessful);
        Assert.Empty(tl.Values);

        Assert.True(tr.Calculate(emptyList).IsSuccessful);
        Assert.Empty(tr.Values);
    }

    [Fact]
    public void TrueIndicators_AreRegisteredInIndicatorFactory()
    {
        var factory = IndicatorFactory.Default;

        Assert.True(factory.IsRegistered(IndicatorType.TrueHigh));
        Assert.True(factory.IsRegistered(IndicatorType.TrueLow));
        Assert.True(factory.IsRegistered(IndicatorType.TrueRange));

        var instTh = factory.Create(IndicatorType.TrueHigh);
        var instTl = factory.Create(IndicatorType.TrueLow);
        var instTr = factory.Create(IndicatorType.TrueRange);

        Assert.NotNull(instTh);
        Assert.IsType<CoreTrueHighIndicator>(instTh);

        Assert.NotNull(instTl);
        Assert.IsType<CoreTrueLowIndicator>(instTl);

        Assert.NotNull(instTr);
        Assert.IsType<CoreTrueRangeIndicator>(instTr);
    }

    [Fact]
    public void TrueIndicators_CalculateSeries_ProducesValidOutput()
    {
        var th = new CoreTrueHighIndicator();
        var tl = new CoreTrueLowIndicator();
        var tr = new CoreTrueRangeIndicator();

        var rawSeries = new List<decimal?> { 100m, 110m, 105m, 120m };

        var resTh = th.CalculateSeries(rawSeries);
        Assert.True(resTh.IsSuccessful);
        Assert.Equal(4, th.Values.Count);
        Assert.Equal(100m, th.Values[0]);
        Assert.Equal(110m, th.Values[1]);

        var resTl = tl.CalculateSeries(rawSeries);
        Assert.True(resTl.IsSuccessful);
        Assert.Equal(4, tl.Values.Count);
        Assert.Equal(100m, tl.Values[0]);

        var resTr = tr.CalculateSeries(rawSeries);
        Assert.True(resTr.IsSuccessful);
        Assert.Equal(4, tr.Values.Count);
        Assert.Equal(0m, tr.Values[0]);
        Assert.Equal(10m, tr.Values[1]); // |110 - 100|
        Assert.Equal(5m, tr.Values[2]);  // |105 - 110|
        Assert.Equal(15m, tr.Values[3]); // |120 - 105|
    }
}
