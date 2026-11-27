using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The snap of the Robust Regression Trend at the object level, and the situations that must NOT snap (settings expansion Run 5): load, data refresh, recalculation, paste, other tools.
/// A range that an older version saved (longer than the period) is only displayed; its anchors never move by themselves.
/// </summary>
public sealed class RobustRangeSnapObjectTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static List<CoreCandleData> Bars(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i % 9) + (i * 0.1m);
            list.Add(new CoreCandleData(Start.AddDays(i), close, close + 1m, close - 1m, close, 1000L));
        }

        return list;
    }

    private static RobustRegressionTrendObject Create(List<CoreCandleData> candles, int first, int last, int period = 30)
        => new(new ChartPoint(candles[first].Timestamp, 150m), new ChartPoint(candles[last].Timestamp, 160m)) { Period = period };

    [Fact]
    public void SnapToPeriod_PutsTheAnchorsOnPeriodBars_KeepsThePrices_AndIsIdempotent()
    {
        List<CoreCandleData> candles = Bars(100);
        using RobustRegressionTrendObject obj = Create(candles, 10, 15, period: 20);

        Assert.True(obj.SnapToPeriod(candles, RangeSnapKind.Placement));

        Assert.Equal(candles[10].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[29].Timestamp, obj.Points[1].Time);
        Assert.Equal(150m, obj.Points[0].Price);
        Assert.Equal(160m, obj.Points[1].Price);
        Assert.Equal(20, obj.Period);
        Assert.False(obj.SnapToPeriod(candles, RangeSnapKind.BodyMove));   // already on the grid: nothing to change
    }

    [Fact]
    public void SnapToPeriod_OfARangeDrawnRightToLeft_KeepsThePointOrder()
    {
        List<CoreCandleData> candles = Bars(100);
        using RobustRegressionTrendObject obj = Create(candles, 40, 20);   // Points[0] is the later anchor

        Assert.True(obj.SnapToPeriod(candles, RangeSnapKind.Placement));

        Assert.Equal(candles[49].Timestamp, obj.Points[0].Time);   // the later anchor: start + 29
        Assert.Equal(candles[20].Timestamp, obj.Points[1].Time);
        Assert.Equal(150m, obj.Points[0].Price);
    }

    [Fact]
    public void SnapToPeriod_WithoutBars_ChangesNothing()
    {
        List<CoreCandleData> candles = Bars(100);
        using RobustRegressionTrendObject obj = Create(candles, 10, 50);
        DateTime t0 = obj.Points[0].Time;
        DateTime t1 = obj.Points[1].Time;

        Assert.False(obj.SnapToPeriod(new List<CoreCandleData>(), RangeSnapKind.Placement));
        Assert.False(DrawingRecalculation.SnapRangeAfterEdit(obj, null, RangeSnapKind.Placement));
        Assert.False(DrawingRecalculation.SnapRangeAfterEdit(obj, new List<CoreCandleData>(), RangeSnapKind.Placement));
        Assert.Equal(t0, obj.Points[0].Time);
        Assert.Equal(t1, obj.Points[1].Time);
    }

    [Fact]
    public void ALegacyLongRange_IsOnlyDisplayed_ByEveryRecalculationEntry_AndNeverMoves()
    {
        List<CoreCandleData> candles = Bars(120);
        using RobustRegressionTrendObject obj = Create(candles, 10, 70);   // 61 bars, period 30
        DateTime t0 = obj.Points[0].Time;
        DateTime t1 = obj.Points[1].Time;
        var context = new DrawingCalculationContext(candles);

        obj.Recalculate(candles);
        Assert.Equal(61, obj.Fit.RangeBars);
        Assert.Equal(30, obj.Fit.EffectiveBars);   // the display rule: the newest Period bars of the longer range
        DeferredComputationRecalculator.TryRecalculate(obj, candles);
        DeferredComputationRecalculator.TryRecalculate(obj, context);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        DrawingRecalculation.AfterMoveCompleted(obj, context);
        DrawingRecalculation.AfterObjectsCreated(new IChartObject[] { obj }, context);

        // A data refresh: more bars arrive and the object is recalculated.
        List<CoreCandleData> refreshed = Bars(150);
        obj.Recalculate(refreshed);

        Assert.Equal(t0, obj.Points[0].Time);
        Assert.Equal(t1, obj.Points[1].Time);
        Assert.Equal(30, obj.Period);
        Assert.Equal(30, obj.Fit.EffectiveBars);
    }

    [Fact]
    public void LoadingAFile_DoesNotSnap_OnEitherPersistencePath()
    {
        List<CoreCandleData> candles = Bars(120);
        using RobustRegressionTrendObject obj = Create(candles, 10, 70, period: 12);   // a long range with a short period
        DateTime t0 = obj.Points[0].Time;
        DateTime t1 = obj.Points[1].Time;

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<RobustRegressionTrendObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.Equal(t0, restored.Points[0].Time);
        Assert.Equal(t1, restored.Points[1].Time);
        Assert.Equal(12, restored.Period);

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("ROB", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        var codecRestored = Assert.IsType<RobustRegressionTrendObject>(materialized.Objects[ChartDrawingContextType.Standard].Single());
        Assert.Equal(t0, codecRestored.Points[0].Time);
        Assert.Equal(t1, codecRestored.Points[1].Time);
        codecRestored.Recalculate(candles);   // the recalculation after the load still leaves the anchors alone
        Assert.Equal(t0, codecRestored.Points[0].Time);
        Assert.Equal(t1, codecRestored.Points[1].Time);
    }

    [Fact]
    public void OtherRangeTools_AreNeverSnapped()
    {
        List<CoreCandleData> candles = Bars(120);
        var cone = new GarchVolatilityConeObject(new ChartPoint(candles[10].Timestamp, 100m), new ChartPoint(candles[20].Timestamp, 100m));
        var weighted = new WeightedRegressionTrendObject(new ChartPoint(candles[10].Timestamp, 100m), new ChartPoint(candles[20].Timestamp, 100m));
        try
        {
            Assert.False(DrawingRecalculation.SnapRangeAfterEdit(cone, candles, RangeSnapKind.Placement));
            Assert.False(DrawingRecalculation.SnapRangeAfterEdit(weighted, candles, RangeSnapKind.BodyMove));
            Assert.Equal(candles[20].Timestamp, cone.Points[1].Time);
            Assert.Equal(candles[20].Timestamp, weighted.Points[1].Time);
            Assert.False(typeof(IPeriodSnappedRange).IsAssignableFrom(typeof(GarchVolatilityConeObject)));
            Assert.False(typeof(IPeriodSnappedRange).IsAssignableFrom(typeof(WeightedRegressionTrendObject)));
            Assert.True(typeof(IPeriodSnappedRange).IsAssignableFrom(typeof(RobustRegressionTrendObject)));
        }
        finally
        {
            cone.Dispose();
            weighted.Dispose();
        }
    }

    [Fact]
    public void ASnappedRange_IsExactlyThePeriodBars_AndTheFitUsesAllOfThem()
    {
        List<CoreCandleData> candles = Bars(120);
        using RobustRegressionTrendObject obj = Create(candles, 10, 70, period: 25);
        Assert.True(obj.SnapToPeriod(candles, RangeSnapKind.PeriodChange));
        obj.Recalculate(candles);

        Assert.Equal(25, obj.Fit.RangeBars);
        Assert.Equal(25, obj.Fit.EffectiveBars);
        Assert.False(obj.Fit.IsTruncated);   // no "Last N bars" badge for a snapped range
    }

    [Fact]
    public void ASnappedRangeAtTheEndOfTheData_IsShorterThanThePeriod_AndSaysSo()
    {
        List<CoreCandleData> candles = Bars(100);
        using RobustRegressionTrendObject obj = Create(candles, 90, 95);
        Assert.True(obj.SnapToPeriod(candles, RangeSnapKind.Placement));
        obj.Recalculate(candles);

        Assert.Equal(candles[99].Timestamp, obj.Points[1].Time);
        Assert.Equal(30, obj.Period);
        Assert.Equal(10, obj.Fit.RangeBars);
        Assert.Equal(10, obj.Fit.EffectiveBars);   // EffectiveBars < Period is shown in the Data tab
    }
}
