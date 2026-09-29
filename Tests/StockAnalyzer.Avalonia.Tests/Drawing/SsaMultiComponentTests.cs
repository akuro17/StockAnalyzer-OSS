using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class SsaMultiComponentTests
{
    private static LinearCoordinateTransform MakeTransform()
        => new LinearCoordinateTransform(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            0m, 100m, 800, 600);

    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m, decimal step = 1m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + i * step + (decimal)(Math.Sin(i * 0.5) * 3.0);
            list.Add(new CoreCandleData(
                baseTime.AddHours(i),
                p - 1m,
                p + 2m,
                p - 2m,
                p,
                1000
            ));
        }
        return list;
    }

    [Fact]
    public void SsaMultiComponentObject_InitialPlacement_Defaults()
    {
        var obj = new SsaMultiComponentObject();

        Assert.Equal(ChartObjectType.SsaMultiComponent, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(20, obj.EmbeddingDimension);
        Assert.Equal(2, obj.NumComponents);
        Assert.Equal(SsaDetrendMode.LeastSquaresLinear, obj.DetrendMethod);
        Assert.True(obj.ShowTrendLayer);
        Assert.True(obj.ShowPrimaryCycleLayer);
        Assert.True(obj.ShowCompositeLayer);
        Assert.True(obj.ShowNoiseBand);
        Assert.Equal(2.0m, obj.NoiseMultiplier);

        Assert.Empty(obj.TrendPath);
        Assert.Empty(obj.PrimaryCyclePath);
        Assert.Empty(obj.CompositePath);
        Assert.Empty(obj.UpperNoiseBandPath);
        Assert.Empty(obj.LowerNoiseBandPath);
    }

    [Fact]
    public void SsaMultiComponentObject_Recalculate_CalculatesAllLayers_And_Diagnostics()
    {
        var candles = MakeSampleCandles(30, startPrice: 100m, step: 2m);
        var obj = new SsaMultiComponentObject
        {
            EmbeddingDimension = 8,
            NumComponents = 2,
            DetrendMethod = SsaDetrendMode.LeastSquaresLinear,
            NoiseMultiplier = 2.0m
        };

        // Select candles from index 0 to 19 (20 candles)
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[19].Timestamp, candles[19].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.Equal(20, obj.TrendPath.Count);
        Assert.Equal(20, obj.PrimaryCyclePath.Count);
        Assert.Equal(20, obj.CompositePath.Count);
        Assert.Equal(20, obj.UpperNoiseBandPath.Count);
        Assert.Equal(20, obj.LowerNoiseBandPath.Count);

        Assert.True(obj.CumulativeVarianceRatio > 0.0);
        Assert.True(obj.ResidualStdDev >= 0.0);
        Assert.Equal(2, obj.EffectiveRank);

        // Verify that UpperNoiseBand >= Composite >= LowerNoiseBand at each point
        for (int i = 0; i < 20; i++)
        {
            Assert.True(obj.UpperNoiseBandPath[i].Y >= obj.CompositePath[i].Y, $"Upper noise band should exceed composite at index {i}");
            Assert.True(obj.CompositePath[i].Y >= obj.LowerNoiseBandPath[i].Y, $"Composite should exceed lower noise band at index {i}");
        }
    }

    [Fact]
    public void SsaMultiComponentObject_HitTest_WithinHorizontalRange_ReturnsTrue()
    {
        var transform = MakeTransform();
        var obj = new SsaMultiComponentObject();
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 6, 0, 0), 50m));
        obj.Points.Add(new ChartPoint(new DateTime(2024, 1, 1, 18, 0, 0), 50m));

        var pMid = transform.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m));
        Assert.True(obj.HitTest(pMid, transform));

        var pOutside = transform.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 1, 22, 0, 0), 50m));
        Assert.False(obj.HitTest(pOutside, transform));
    }

    [Fact]
    public void SsaMultiComponentObject_Translate_ShiftsAllPaths()
    {
        var candles = MakeSampleCandles(20);
        var obj = new SsaMultiComponentObject
        {
            EmbeddingDimension = 6,
            NumComponents = 2
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));
        obj.Recalculate(candles);

        double origTrendY = obj.TrendPath[0].Y;
        var delta = TimeSpan.FromHours(2);
        decimal priceDelta = 5m;

        obj.Translate(delta, priceDelta);

        Assert.Equal(origTrendY + (double)priceDelta, obj.TrendPath[0].Y, precision: 4);
    }

    [Fact]
    public void SsaMultiComponentObject_GetCalculatedValues_ReturnsExpectedMetrics()
    {
        var candles = MakeSampleCandles(20);
        var obj = new SsaMultiComponentObject
        {
            EmbeddingDimension = 6,
            NumComponents = 2
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));
        obj.Recalculate(candles);

        var values = obj.GetCalculatedValues();
        Assert.NotNull(values);
        Assert.Contains(values, v => v.Label == "Samples" && v.FormattedText == "15");
        Assert.Contains(values, v => v.Label == "Embedding Dimension" && v.FormattedText == "6");
        Assert.Contains(values, v => v.Label == "Components (r)" && v.FormattedText == "2");
        Assert.Contains(values, v => v.Label == "SNR (dB)");
        Assert.Contains(values, v => v.Label == "Signal Purity");
        Assert.Contains(values, v => v.Label == "Dominant Period");
    }

    [Fact]
    public void DeferredComputationRecalculator_TryRecalculate_HandlesSsaMultiComponent()
    {
        var candles = MakeSampleCandles(20);
        var obj = new SsaMultiComponentObject
        {
            EmbeddingDimension = 6,
            NumComponents = 2
        };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[14].Timestamp, candles[14].Close));

        bool result = DeferredComputationRecalculator.TryRecalculate(obj, candles);
        Assert.True(result);
        Assert.Equal(15, obj.TrendPath.Count);
    }

    [Fact]
    public void SsaMultiComponentSettingsPanelDefinition_CanHandle_ValidatesObjectType()
    {
        var panelDef = new StockAnalyzer.Avalonia.Views.Dialogs.SsaMultiComponentSettingsPanelDefinition();
        Assert.True(panelDef.CanHandle(new SsaMultiComponentObject()));
        Assert.False(panelDef.CanHandle(new StockAnalyzer.Avalonia.Drawing.Objects.SsaProjectionObject()));
    }
}
