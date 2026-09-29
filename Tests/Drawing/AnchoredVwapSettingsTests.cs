using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class AnchoredVwapSettingsTests
{
    private static List<CoreCandleData> CreateSampleCandles()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < 10; i++)
        {
            // Open != High != Low != Close
            decimal open = 100m + i * 5m;
            decimal high = open + 10m;
            decimal low = open - 4m;
            decimal close = open + 6m;
            long volume = 1000 + i * 100;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), open, high, low, close, volume));
        }
        return candles;
    }

    [Fact]
    public void AnchoredVwapObject_DefaultsToTypicalPriceSource()
    {
        var anchor = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var vwap = new AnchoredVwapObject(anchor);

        Assert.Equal(PriceType.Typical, vwap.PriceSource);
    }

    [Fact]
    public void CalculateAnchoredVwap_WithDifferentPriceTypes_ProducesDistinctCurves()
    {
        var candles = CreateSampleCandles();

        var typicalCurve = VolumeAnalysis.CalculateAnchoredVwap(candles, PriceType.Typical);
        var closeCurve = VolumeAnalysis.CalculateAnchoredVwap(candles, PriceType.Close);
        var openCurve = VolumeAnalysis.CalculateAnchoredVwap(candles, PriceType.Open);

        Assert.Equal(candles.Count, typicalCurve.Count);
        Assert.Equal(candles.Count, closeCurve.Count);
        Assert.Equal(candles.Count, openCurve.Count);

        // Verify that distinct price sources produce different VWAP prices
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.NotEqual(typicalCurve[i].Vwap, closeCurve[i].Vwap);
            Assert.NotEqual(typicalCurve[i].Vwap, openCurve[i].Vwap);
            Assert.NotEqual(closeCurve[i].Vwap, openCurve[i].Vwap);
        }
    }

    [Fact]
    public void CalculateAnchoredVwap_WithoutPriceType_MatchesExplicitTypical()
    {
        var candles = CreateSampleCandles();

        // Default parameter call
        var defaultCurve = VolumeAnalysis.CalculateAnchoredVwap(candles);
        // Explicit Typical call
        var typicalCurve = VolumeAnalysis.CalculateAnchoredVwap(candles, PriceType.Typical);

        Assert.Equal(typicalCurve.Count, defaultCurve.Count);
        for (int i = 0; i < typicalCurve.Count; i++)
        {
            Assert.Equal(typicalCurve[i].Time, defaultCurve[i].Time);
            Assert.Equal(typicalCurve[i].Vwap, defaultCurve[i].Vwap);
        }
    }

    [Fact]
    public void Recalculate_UsesConfiguredPriceSource()
    {
        var candles = CreateSampleCandles();
        var anchor = new ChartPoint(candles[2].Timestamp, 100m);
        var vwap = new AnchoredVwapObject(anchor)
        {
            PriceSource = PriceType.Close
        };

        vwap.Recalculate(candles);

        var expectedCurve = VolumeAnalysis.CalculateAnchoredVwap(candles.GetRange(2, 8), PriceType.Close);
        Assert.Equal(expectedCurve.Count, vwap.VwapCurve.Count);
        for (int i = 0; i < expectedCurve.Count; i++)
        {
            Assert.Equal(expectedCurve[i].Vwap, vwap.VwapCurve[i].Price);
        }
    }

    [Fact]
    public void AnchoredVwapSettingsPanelDefinition_CanHandle_OnlyAnchoredVwapObject()
    {
        var definition = new AnchoredVwapSettingsPanelDefinition();

        var vwap = new AnchoredVwapObject(new ChartPoint(DateTime.Now, 100m));
        var trendLine = new TrendLineObject(new ChartPoint(DateTime.Now, 100m), new ChartPoint(DateTime.Now, 110m));

        Assert.True(definition.CanHandle(vwap));
        Assert.False(definition.CanHandle(trendLine));
    }
}
