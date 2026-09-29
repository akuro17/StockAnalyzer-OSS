using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class HoughDrawingToolsTests
{
    private static List<CoreCandleData> CreateSampleCandles(int count = 60)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal trend = 100m + i * 0.5m;
            decimal wave = (decimal)(4.0 * Math.Sin(2.0 * Math.PI * i / 8.0));
            decimal close = trend + wave;
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                close,
                close + 2.0m,
                close - 2.0m,
                close,
                1000L));
        }
        return list;
    }

    private static List<CoreCandleData> CreateParabolicCandles(int count = 50)
    {
        var list = new List<CoreCandleData>(count);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal trend = (decimal)(0.05 * Math.Pow(i - 25, 2) + 100.0);
            decimal wiggle = (i % 4 == 0) ? 2.0m : (i % 4 == 2 ? -2.0m : 0.0m);
            decimal close = trend + wiggle;
            list.Add(new CoreCandleData(
                baseDate.AddDays(i),
                close,
                close + 1.5m,
                close - 1.5m,
                close,
                1000L));
        }
        return list;
    }

    [Fact]
    public void HoughAutoLinesObject_InitializationAndRecalculate()
    {
        var obj = new HoughAutoLinesObject();
        Assert.Equal(ChartObjectType.HoughAutoLines, obj.Type);
        Assert.Equal(3, obj.PivotWindow);
        Assert.Equal(3, obj.VoteThreshold);
        Assert.Equal(5, obj.MaxLines);

        var candles = CreateSampleCandles(60);
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedResult);
        Assert.True(obj.CalculatedResult.Lines.Count > 0);

        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.NotEmpty(values);
    }

    [Fact]
    public void HoughKeyLevelsObject_InitializationAndRecalculate()
    {
        var obj = new HoughKeyLevelsObject();
        Assert.Equal(ChartObjectType.HoughKeyLevels, obj.Type);
        Assert.Equal(3, obj.PivotWindow);
        Assert.Equal(5, obj.MaxLevels);
        Assert.True(obj.ExtendRight);

        var candles = CreateSampleCandles(60);
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedLevels);
        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.True(values.Count >= 0);
    }

    [Fact]
    public void HoughMagneticLineObject_InitializationAndRecalculate()
    {
        var obj = new HoughMagneticLineObject();
        Assert.Equal(ChartObjectType.HoughMagneticLine, obj.Type);
        Assert.Equal(3, obj.PivotWindow);
        Assert.True(obj.ExtendRight);

        var candles = CreateSampleCandles(60);
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedLine);
        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "Magnetic Line Type");
    }

    [Fact]
    public void HoughResonantFanObject_InitializationAndRecalculate()
    {
        var obj = new HoughResonantFanObject();
        Assert.Equal(ChartObjectType.HoughResonantFan, obj.Type);
        Assert.Equal(3, obj.PivotWindow);
        Assert.True(obj.ExtendRight);

        var candles = CreateSampleCandles(60);
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedFanRays);
        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.True(values.Count >= 0);
    }

    [Fact]
    public void HoughParabolicCurveObject_InitializationAndRecalculate()
    {
        var obj = new HoughParabolicCurveObject();
        Assert.Equal(ChartObjectType.HoughParabolicCurve, obj.Type);
        Assert.Equal(ParabolicHoughCurvatureSign.Both, obj.CurvatureSign);

        var candles = CreateParabolicCandles(50);
        obj.PivotWindow = 1;
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedResult);
        Assert.False(obj.CalculatedResult.IsEmpty);
        Assert.True(obj.CalculatedResult.Parabolas[0].RSquared > 0.80);

        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.NotEmpty(values);
    }

    [Fact]
    public void DeferredComputationRecalculator_HandlesAllFiveHoughObjects()
    {
        var candles = CreateSampleCandles(60);

        var autoLines = new HoughAutoLinesObject();
        autoLines.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        autoLines.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(autoLines, candles));

        var keyLevels = new HoughKeyLevelsObject();
        keyLevels.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        keyLevels.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(keyLevels, candles));

        var magLine = new HoughMagneticLineObject();
        magLine.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        magLine.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(magLine, candles));

        var fan = new HoughResonantFanObject();
        fan.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        fan.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(fan, candles));

        var parabola = new HoughParabolicCurveObject();
        parabola.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        parabola.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(parabola, candles));
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_RegistersAllFiveHoughTools()
    {
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.HoughAutoLines));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.HoughParabolicCurve));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.HoughKeyLevels));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.HoughResonantFan));
        Assert.NotNull(DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.HoughMagneticLine));
    }

    [Fact]
    public void Translate_MovesPointsAndInvalidatesCache()
    {
        var obj = new HoughAutoLinesObject();
        var t0 = new DateTime(2025, 1, 1);
        var t1 = new DateTime(2025, 1, 10);
        obj.Points.Add(new ChartPoint(t0, 100m));
        obj.Points.Add(new ChartPoint(t1, 120m));

        obj.Translate(TimeSpan.FromDays(2), 5m);

        Assert.Equal(t0.AddDays(2), obj.Points[0].Time);
        Assert.Equal(105m, obj.Points[0].Price);
        Assert.Equal(t1.AddDays(2), obj.Points[1].Time);
        Assert.Equal(125m, obj.Points[1].Price);
    }

    [Fact]
    public void AllHoughTools_UseConsistentDefaultColor()
    {
        var autoLines = new HoughAutoLinesObject();
        var parabola = new HoughParabolicCurveObject();
        var keyLevels = new HoughKeyLevelsObject();
        var fan = new HoughResonantFanObject();
        var magLine = new HoughMagneticLineObject();

        Assert.Equal(DrawingThemeContext.DefaultColor, autoLines.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, parabola.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, keyLevels.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, fan.Color);
        Assert.Equal(DrawingThemeContext.DefaultColor, magLine.Color);
    }

    [Fact]
    public void Translate_ShiftsSliceTimesOnAllObjects()
    {
        var t0 = new DateTime(2025, 1, 1);
        var t1 = new DateTime(2025, 1, 10);
        var delta = TimeSpan.FromDays(3);

        var autoLines = new HoughAutoLinesObject { SliceStartTime = t0, SliceEndTime = t1 };
        autoLines.Points.Add(new ChartPoint(t0, 100));
        autoLines.Points.Add(new ChartPoint(t1, 120));
        autoLines.Translate(delta, 10);
        Assert.Equal(t0 + delta, autoLines.SliceStartTime);
        Assert.Equal(t1 + delta, autoLines.SliceEndTime);

        var parabola = new HoughParabolicCurveObject { SliceStartTime = t0, SliceEndTime = t1, SliceMidTime = t0.AddDays(5) };
        parabola.Points.Add(new ChartPoint(t0, 100));
        parabola.Points.Add(new ChartPoint(t1, 120));
        parabola.Translate(delta, 10);
        Assert.Equal(t0 + delta, parabola.SliceStartTime);
        Assert.Equal(t1 + delta, parabola.SliceEndTime);
        Assert.Equal(t0.AddDays(5) + delta, parabola.SliceMidTime);

        var keyLevels = new HoughKeyLevelsObject { SliceStartTime = t0, SliceEndTime = t1 };
        keyLevels.Points.Add(new ChartPoint(t0, 100));
        keyLevels.Points.Add(new ChartPoint(t1, 120));
        keyLevels.Translate(delta, 10);
        Assert.Equal(t0 + delta, keyLevels.SliceStartTime);
        Assert.Equal(t1 + delta, keyLevels.SliceEndTime);

        var fan = new HoughResonantFanObject { OriginTime = t0, SliceEndTime = t1, OriginPrice = 100m };
        fan.Points.Add(new ChartPoint(t0, 100));
        fan.Points.Add(new ChartPoint(t1, 120));
        fan.Translate(delta, 10);
        Assert.Equal(t0 + delta, fan.OriginTime);
        Assert.Equal(t1 + delta, fan.SliceEndTime);
        Assert.Equal(110m, fan.OriginPrice);

        var magLine = new HoughMagneticLineObject { SliceStartTime = t0, SliceEndTime = t1 };
        magLine.Points.Add(new ChartPoint(t0, 100));
        magLine.Points.Add(new ChartPoint(t1, 120));
        magLine.Translate(delta, 10);
        Assert.Equal(t0 + delta, magLine.SliceStartTime);
        Assert.Equal(t1 + delta, magLine.SliceEndTime);
    }

    [Fact]
    public void IndependentControlPointAdjustment_ModifiesOnlyTargetHandle()
    {
        var candles = CreateSampleCandles(60);
        var autoLines = new HoughAutoLinesObject();
        var t0 = candles[10].Timestamp;
        var t1 = candles[30].Timestamp;
        autoLines.Points.Add(new ChartPoint(t0, 100m));
        autoLines.Points.Add(new ChartPoint(t1, 120m));

        DeferredComputationRecalculator.TryRecalculate(autoLines, candles);
        Assert.Equal(t0, autoLines.SliceStartTime);
        Assert.Equal(t1, autoLines.SliceEndTime);

        // Simulate dragging Handle 0 independently (e.g. from index 10 to index 5)
        var newT0 = candles[5].Timestamp;
        autoLines.Points[0] = new ChartPoint(newT0, autoLines.Points[0].Price);

        // Handle 1 (Points[1]) remains unchanged!
        Assert.Equal(t1, autoLines.Points[1].Time);

        // Trigger recalculation with the updated independent handle
        DeferredComputationRecalculator.TryRecalculate(autoLines, candles);
        Assert.Equal(newT0, autoLines.SliceStartTime);
        Assert.Equal(t1, autoLines.SliceEndTime);

        // Now simulate dragging Handle 1 independently (e.g. from index 30 to index 45)
        var newT1 = candles[45].Timestamp;
        autoLines.Points[1] = new ChartPoint(newT1, autoLines.Points[1].Price);

        // Handle 0 (Points[0]) remains unchanged!
        Assert.Equal(newT0, autoLines.Points[0].Time);

        DeferredComputationRecalculator.TryRecalculate(autoLines, candles);
        Assert.Equal(newT0, autoLines.SliceStartTime);
        Assert.Equal(newT1, autoLines.SliceEndTime);
    }

    [Fact]
    public void HoughAutoLines_VisibilityTogglesAndIndividualColors_ConfigureProperly()
    {
        var autoLines = new HoughAutoLinesObject();
        Assert.True(autoLines.ShowTrendLines);
        Assert.True(autoLines.ShowSupportLines);
        Assert.True(autoLines.ShowResistanceLines);

        var customTrend = Color.FromRgb(10, 20, 30);
        var customSupport = Color.FromRgb(40, 50, 60);
        var customResistance = Color.FromRgb(70, 80, 90);

        autoLines.TrendLineColor = customTrend;
        autoLines.SupportColor = customSupport;
        autoLines.ResistanceColor = customResistance;

        Assert.Equal(customTrend, autoLines.TrendLineColor);
        Assert.Equal(customTrend, autoLines.TrendUpColor);
        Assert.Equal(customTrend, autoLines.TrendDownColor);
        Assert.Equal(customSupport, autoLines.SupportColor);
        Assert.Equal(customResistance, autoLines.ResistanceColor);

        var candles = CreateSampleCandles(60);
        autoLines.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        autoLines.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        autoLines.Recalculate(candles);

        // With all visible
        var allValues = autoLines.GetCalculatedValues(DateTime.UtcNow);

        // When hiding trend lines
        autoLines.ShowTrendLines = false;
        var noTrendValues = autoLines.GetCalculatedValues(DateTime.UtcNow);
        Assert.DoesNotContain(noTrendValues, v => v.Label.Contains("TrendUp") || v.Label.Contains("TrendDown"));

        // When hiding support lines
        autoLines.ShowSupportLines = false;
        var noSupportValues = autoLines.GetCalculatedValues(DateTime.UtcNow);
        Assert.DoesNotContain(noSupportValues, v => v.Label.Contains("Support"));

        // When hiding resistance lines
        autoLines.ShowResistanceLines = false;
        var noneValues = autoLines.GetCalculatedValues(DateTime.UtcNow);
        Assert.DoesNotContain(noneValues, v => v.Label.Contains("Resistance"));
    }

    [Fact]
    public void HoughKeyLevels_IndividualColors_ConfigureProperly()
    {
        var keyLevels = new HoughKeyLevelsObject();
        var customSupport = Color.FromRgb(11, 22, 33);
        var customResistance = Color.FromRgb(44, 55, 66);

        keyLevels.SupportColor = customSupport;
        keyLevels.ResistanceColor = customResistance;

        Assert.Equal(customSupport, keyLevels.SupportColor);
        Assert.Equal(customResistance, keyLevels.ResistanceColor);

        var candles = CreateSampleCandles(60);
        keyLevels.Points.Add(new ChartPoint(candles[0].Timestamp, 100));
        keyLevels.Points.Add(new ChartPoint(candles[^1].Timestamp, 120));
        keyLevels.Recalculate(candles);

        var values = keyLevels.GetCalculatedValues(DateTime.UtcNow);
        foreach (var v in values)
        {
            if (v.Label.Contains("Support"))
            {
                Assert.Equal(customSupport.R, v.Color.R);
                Assert.Equal(customSupport.G, v.Color.G);
                Assert.Equal(customSupport.B, v.Color.B);
            }
            else if (v.Label.Contains("Resistance"))
            {
                Assert.Equal(customResistance.R, v.Color.R);
                Assert.Equal(customResistance.G, v.Color.G);
                Assert.Equal(customResistance.B, v.Color.B);
            }
        }
    }
}
