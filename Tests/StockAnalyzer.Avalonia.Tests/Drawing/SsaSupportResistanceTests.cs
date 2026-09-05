using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class SsaSupportResistanceTests
{
    private List<CoreCandleData> CreateSampleCandles(int count = 60, double basePrice = 100.0)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = (decimal)(basePrice + 10.0 * Math.Sin(2.0 * Math.PI * i / 12.0));
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                price,
                price + 1.0m,
                price - 1.0m,
                price,
                1000L));
        }
        return list;
    }

    [Fact]
    public void SsaSupportResistanceObject_DefaultProperties_InitializedCorrectly()
    {
        var obj = new SsaSupportResistanceObject();
        Assert.Equal(ChartObjectType.SsaSupportResistance, obj.Type);
        Assert.Equal(SsaSupportResistanceMode.StructuralPivots, obj.Mode);
        Assert.Equal(15, obj.EmbeddingDimension);
        Assert.Equal(2, obj.NumComponents);
        Assert.True(obj.AutoRank);
        Assert.Equal(SsaDetrendMode.LeastSquaresLinear, obj.DetrendMethod);
        Assert.True(obj.ExtendLinesToRight);
        Assert.Equal(2, obj.MaxLevelsPerSide);
        Assert.Equal(0.5m, obj.ClusterTolerance);
        Assert.Equal(2.0m, obj.Multiplier);
        Assert.Equal(20, obj.FutureSteps);
    }

    [Fact]
    public void SsaStructuralPivotsObject_HasCorrectTypeAndDefaultMode()
    {
        var obj = new SsaStructuralPivotsObject();
        Assert.Equal(ChartObjectType.SsaStructuralPivots, obj.Type);
        Assert.Equal(SsaSupportResistanceMode.StructuralPivots, obj.Mode);
    }

    [Fact]
    public void SsaDynamicEnvelopesObject_HasCorrectTypeAndDefaultMode()
    {
        var obj = new SsaDynamicEnvelopesObject();
        Assert.Equal(ChartObjectType.SsaDynamicEnvelopes, obj.Type);
        Assert.Equal(SsaSupportResistanceMode.DynamicEnvelopes, obj.Mode);
        Assert.Equal(2.0m, obj.Multiplier);
    }

    [Fact]
    public void SsaDynamicEnvelopesObject_Multiplier_ModifiesBandWidth()
    {
        var candles = CreateSampleCandles(60);
        var obj1 = new SsaDynamicEnvelopesObject { Multiplier = 1.0m };
        obj1.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj1.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        obj1.Recalculate(candles, TimeSpan.FromDays(1));

        var obj2 = new SsaDynamicEnvelopesObject { Multiplier = 3.0m };
        obj2.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj2.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        obj2.Recalculate(candles, TimeSpan.FromDays(1));

        double width1 = obj1.CalculatedResult!.UpperBand[0].Y - obj1.CalculatedResult.LowerBand[0].Y;
        double width2 = obj2.CalculatedResult!.UpperBand[0].Y - obj2.CalculatedResult.LowerBand[0].Y;

        Assert.True(width2 > width1);
    }

    [Fact]
    public void SsaProjectedTargetsObject_HasCorrectTypeAndDefaultMode()
    {
        var obj = new SsaProjectedTargetsObject();
        Assert.Equal(ChartObjectType.SsaProjectedTargets, obj.Type);
        Assert.Equal(SsaSupportResistanceMode.ProjectedTargets, obj.Mode);
        Assert.True(obj.ExtendLinesToRight);
    }

    [Fact]
    public void SsaProjectedTargetsObject_ExtendLinesToRight_CanBeToggled()
    {
        var obj = new SsaProjectedTargetsObject { ExtendLinesToRight = false };
        Assert.False(obj.ExtendLinesToRight);
        obj.ExtendLinesToRight = true;
        Assert.True(obj.ExtendLinesToRight);
    }

    [Fact]
    public void SsaSupportResistanceObject_Recalculate_CalculatesAndPopulatesResult()
    {
        var candles = CreateSampleCandles(60);
        var obj = new SsaSupportResistanceObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.NotNull(obj.CalculatedResult);
        Assert.False(obj.CalculatedResult.IsEmpty);
        Assert.NotEmpty(obj.CalculatedResult.ResistanceLevels);
        Assert.NotEmpty(obj.CalculatedResult.SupportLevels);
    }

    [Fact]
    public void SsaSupportResistanceObject_GetCalculatedValues_ReturnsExpectedDataWindowMetrics()
    {
        var candles = CreateSampleCandles(60);
        var obj = new SsaSupportResistanceObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles, TimeSpan.FromDays(1));
        var values = obj.GetCalculatedValues(candles[^1].Timestamp, candles[^1].Close);

        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "SSA S/R Mode");
        Assert.Contains(values, v => v.Key == "SSA Active Resistance");
        Assert.Contains(values, v => v.Key == "SSA Active Support");
        Assert.Contains(values, v => v.Key == "SSA Residual Noise (σ)");
        Assert.Contains(values, v => v.Key == "SSA Separability");
    }

    [Fact]
    public void SsaSupportResistanceObject_ModeSwitching_UpdatesCalculationAndValues()
    {
        var candles = CreateSampleCandles(60);
        var obj = new SsaSupportResistanceObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        // Mode 2: Dynamic Envelopes
        obj.Mode = SsaSupportResistanceMode.DynamicEnvelopes;
        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.NotNull(obj.CalculatedResult);
        Assert.Equal(SsaSupportResistanceMode.DynamicEnvelopes, obj.CalculatedResult.Mode);
        Assert.NotEmpty(obj.CalculatedResult.UpperBand);
        Assert.NotEmpty(obj.CalculatedResult.LowerBand);

        // Mode 3: Projected Targets
        obj.Mode = SsaSupportResistanceMode.ProjectedTargets;
        obj.Recalculate(candles, TimeSpan.FromDays(1));

        Assert.NotNull(obj.CalculatedResult);
        Assert.Equal(SsaSupportResistanceMode.ProjectedTargets, obj.CalculatedResult.Mode);
        Assert.NotEmpty(obj.CalculatedResult.ProjectedPath);
        Assert.Single(obj.CalculatedResult.ResistanceLevels);
        Assert.Single(obj.CalculatedResult.SupportLevels);
    }

    [Fact]
    public void SsaSupportResistanceObject_Translate_ShiftsPointsAndInvalidatesCache()
    {
        var candles = CreateSampleCandles(60);
        var obj = new SsaSupportResistanceObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, 100m));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, 110m));

        obj.Recalculate(candles, TimeSpan.FromDays(1));
        Assert.NotNull(obj.CalculatedResult);

        obj.Translate(TimeSpan.FromDays(5), 10m);

        Assert.Equal(110m, obj.Points[0].Price);
        Assert.Equal(120m, obj.Points[1].Price);
        Assert.Null(obj.CalculatedResult);
    }

    [Fact]
    public void DeferredComputationRecalculator_HandlesSpecializedSsaObjects()
    {
        var candles = CreateSampleCandles(60);
        var p1 = new SsaStructuralPivotsObject();
        p1.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        p1.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        var p2 = new SsaDynamicEnvelopesObject();
        p2.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        p2.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        var p3 = new SsaProjectedTargetsObject();
        p3.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        p3.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        Assert.True(DeferredComputationRecalculator.TryRecalculate(p1, candles));
        Assert.NotNull(p1.CalculatedResult);
        Assert.Equal(SsaSupportResistanceMode.StructuralPivots, p1.CalculatedResult.Mode);

        Assert.True(DeferredComputationRecalculator.TryRecalculate(p2, candles));
        Assert.NotNull(p2.CalculatedResult);
        Assert.Equal(SsaSupportResistanceMode.DynamicEnvelopes, p2.CalculatedResult.Mode);

        Assert.True(DeferredComputationRecalculator.TryRecalculate(p3, candles));
        Assert.NotNull(p3.CalculatedResult);
        Assert.Equal(SsaSupportResistanceMode.ProjectedTargets, p3.CalculatedResult.Mode);
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_ContainsAllSpecializedSsaTools()
    {
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.SsaSupportResistance));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.SsaStructuralPivots));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.SsaDynamicEnvelopes));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.SsaProjectedTargets));
    }

    [Fact]
    public void DrawingSettingsPanelRegistry_ResolvesSpecializedPanelsCorrectly()
    {
        var registry = new StockAnalyzer.Avalonia.Services.DrawingSettingsPanelRegistry();
        registry.Register(new StockAnalyzer.Avalonia.Views.Dialogs.SsaStructuralPivotsSettingsPanelDefinition());
        registry.Register(new StockAnalyzer.Avalonia.Views.Dialogs.SsaDynamicEnvelopesSettingsPanelDefinition());
        registry.Register(new StockAnalyzer.Avalonia.Views.Dialogs.SsaProjectedTargetsSettingsPanelDefinition());
        registry.Register(new StockAnalyzer.Avalonia.Views.Dialogs.SsaSupportResistanceSettingsPanelDefinition());

        var p1 = new SsaStructuralPivotsObject();
        var p2 = new SsaDynamicEnvelopesObject();
        var p3 = new SsaProjectedTargetsObject();
        var baseObj = new SsaSupportResistanceObject();

        Assert.IsType<StockAnalyzer.Avalonia.Views.Dialogs.SsaStructuralPivotsSettingsPanelDefinition>(registry.Resolve(p1));
        Assert.IsType<StockAnalyzer.Avalonia.Views.Dialogs.SsaDynamicEnvelopesSettingsPanelDefinition>(registry.Resolve(p2));
        Assert.IsType<StockAnalyzer.Avalonia.Views.Dialogs.SsaProjectedTargetsSettingsPanelDefinition>(registry.Resolve(p3));
        Assert.IsType<StockAnalyzer.Avalonia.Views.Dialogs.SsaSupportResistanceSettingsPanelDefinition>(registry.Resolve(baseObj));
    }

    [Fact]
    public void SsaSupportResistanceRenderer_Render_DoesNotThrowAndHandlesExtendLinesToRight()
    {
        var candles = CreateSampleCandles(60);
        var obj = new SsaProjectedTargetsObject { ExtendLinesToRight = true };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        obj.Recalculate(candles, TimeSpan.FromDays(1));

        var transform = new LinearCoordinateTransform(
            candles[0].Timestamp, candles[^1].Timestamp, 50m, 150m, 1000, 600);

        using var bitmap = new SkiaSharp.SKBitmap(1000, 600);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);

        var renderer = new StockAnalyzer.Avalonia.Drawing.Renderers.SsaSupportResistanceRenderer();
        var ex1 = Record.Exception(() => renderer.Render(canvas, obj, transform, isSelected: true));
        Assert.Null(ex1);

        obj.ExtendLinesToRight = false;
        var ex2 = Record.Exception(() => renderer.Render(canvas, obj, transform, isSelected: false));
        Assert.Null(ex2);
    }

    [Fact]
    public void SsaSupportResistanceEngine_PopulatesPreformattedLabels()
    {
        var candles = CreateSampleCandles(60);
        var samples = candles.Select(c => (double)c.Close).ToArray();
        var times = candles.Select(c => c.Timestamp).ToArray();

        // Mode 1
        var res1 = SsaSupportResistanceEngine.Calculate(
            samples, times, SsaSupportResistanceMode.StructuralPivots, embeddingDimension: 15, numComponents: 2);
        Assert.False(res1.IsEmpty);
        foreach (var lvl in res1.ResistanceLevels)
        {
            Assert.StartsWith("R", lvl.Label);
            Assert.Contains("Hits:", lvl.Label);
        }
        foreach (var lvl in res1.SupportLevels)
        {
            Assert.StartsWith("S", lvl.Label);
            Assert.Contains("Hits:", lvl.Label);
        }

        // Mode 2
        var res2 = SsaSupportResistanceEngine.Calculate(
            samples, times, SsaSupportResistanceMode.DynamicEnvelopes, embeddingDimension: 15, numComponents: 2);
        Assert.False(res2.IsEmpty);
        Assert.StartsWith("Upper:", res2.ResistanceLevels[0].Label);
        Assert.StartsWith("Lower:", res2.SupportLevels[0].Label);

        // Mode 3
        var res3 = SsaSupportResistanceEngine.Calculate(
            samples, times, SsaSupportResistanceMode.ProjectedTargets, embeddingDimension: 15, numComponents: 2, futureSteps: 20);
        Assert.False(res3.IsEmpty);
        Assert.StartsWith("Target R:", res3.ResistanceLevels[0].Label);
        Assert.StartsWith("Target S:", res3.SupportLevels[0].Label);
    }
}
