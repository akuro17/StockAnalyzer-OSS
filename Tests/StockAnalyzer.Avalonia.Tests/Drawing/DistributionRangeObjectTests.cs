using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class DistributionRangeObjectTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static List<CoreCandleData> MakeWavyCandles(int count = 45)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + i * 0.3m + Math.Round((decimal)(Math.Sin(i * 0.4) * 3.5), 4);
            candles.Add(new CoreCandleData(BaseTime.AddDays(i), close - 0.2m, close + 0.8m, close - 0.8m, close, 1000L + i));
        }
        return candles;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 800, int height = 600)
        => new(BaseTime, BaseTime.AddDays(50), 80m, 140m, width, height);

    [Fact]
    public void CategoryAndBehaviorRegistry_ExposeDistributionRangeUnderCycles()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var cycles = categories.Single(c => c.NameKey == "DrawCat_Cycles");
        var toolInfo = cycles.Tools.SingleOrDefault(t => t.Tool == DrawingTool.DistributionRange);

        Assert.NotNull(toolInfo);
        Assert.Equal("DrawTool_DistributionRange", toolInfo!.NameKey);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.DistributionRange);
        Assert.IsType<DistributionRangeBehavior>(behavior);
        Assert.Equal(2, behavior!.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void PointsReversal_ProducesIdenticalResultAndCalculatedValues()
    {
        var candles = MakeWavyCandles(40);
        var p1 = new ChartPoint(candles[5].Timestamp, candles[5].Close);
        var p2 = new ChartPoint(candles[35].Timestamp, candles[35].Close);

        using var forward = new DistributionRangeObject(p1, p2);
        using var backward = new DistributionRangeObject(p2, p1);
        forward.Recalculate(candles);
        backward.Recalculate(candles);

        Assert.Equal(forward.CachedResult.SampleCount, backward.CachedResult.SampleCount);
        Assert.Equal(forward.CachedResult.Mean, backward.CachedResult.Mean);
        Assert.Equal(forward.CachedResult.Skewness, backward.CachedResult.Skewness);
        Assert.Equal(forward.CachedResult.ExcessKurtosis, backward.CachedResult.ExcessKurtosis);
        Assert.Equal(
            forward.GetCalculatedValues(candles[10].Timestamp),
            backward.GetCalculatedValues(candles[10].Timestamp));
    }

    [Fact]
    public void HitTest_MatchesRangeBoundsAndToleranceOnly()
    {
        var candles = MakeWavyCandles(40);
        var transform = MakeTransform();
        var p1 = new ChartPoint(candles[5].Timestamp, 90m);
        var p2 = new ChartPoint(candles[25].Timestamp, 110m);
        using var obj = new DistributionRangeObject(p1, p2);
        obj.Recalculate(candles);

        double x1 = transform.ChartToScreen(p1).X;
        double x2 = transform.ChartToScreen(p2).X;
        double midX = (x1 + x2) * 0.5;

        Assert.True(obj.HitTest(new Point(midX, 300), transform));
        Assert.True(obj.HitTest(new Point(x1 - 3, 300), transform, tolerance: 5));
        Assert.False(obj.HitTest(new Point(x1 - 15, 300), transform, tolerance: 5));
        Assert.False(obj.HitTest(new Point(midX, 700), transform, tolerance: 5));

        obj.IsVisible = false;
        Assert.False(obj.HitTest(new Point(midX, 300), transform));
    }

    [Theory]
    [InlineData(DistributionDataMode.LogReturn, DistributionDisplayStyle.Histogram, false)]
    [InlineData(DistributionDataMode.LogReturn, DistributionDisplayStyle.HistogramWithCurve, false)]
    [InlineData(DistributionDataMode.LogReturn, DistributionDisplayStyle.HorizontalPriceProfile, false)]
    [InlineData(DistributionDataMode.Price, DistributionDisplayStyle.HorizontalPriceProfile, false)]
    [InlineData(DistributionDataMode.Price, DistributionDisplayStyle.HorizontalPriceProfile, true)]
    public void Render_AcrossModesAndNarrowViewport_DoesNotThrow(
        DistributionDataMode mode,
        DistributionDisplayStyle style,
        bool rtl)
    {
        var candles = MakeWavyCandles(40);
        using var obj = new DistributionRangeObject(
            new ChartPoint(candles[2].Timestamp, candles[2].Close),
            new ChartPoint(candles[35].Timestamp, candles[35].Close))
        {
            DataMode = mode,
            DisplayStyle = style,
            RightToLeft = rtl,
            IsSelected = true
        };
        obj.Recalculate(candles);

        // Standard viewport
        using (var bmp = new SKBitmap(800, 600))
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.ClipRect(new SKRect(0, 0, 800, 600));
            obj.Render(canvas, MakeTransform(800, 600));
        }

        // Narrow viewport guard (label box leaves < MinPlotHeightPx)
        using (var tinyBmp = new SKBitmap(400, 48))
        using (var tinyCanvas = new SKCanvas(tinyBmp))
        {
            tinyCanvas.ClipRect(new SKRect(0, 0, 400, 48));
            obj.Render(tinyCanvas, MakeTransform(400, 48));
        }
    }

    [Fact]
    public async Task RenderAndDispose_ConcurrentSafety_DoesNotThrow()
    {
        var candles = MakeWavyCandles(40);
        var obj = new DistributionRangeObject(
            new ChartPoint(candles[1].Timestamp, 95m),
            new ChartPoint(candles[30].Timestamp, 115m));
        obj.Recalculate(candles);
        var transform = MakeTransform();

        Task renderTask = Task.Run(() =>
        {
            using var bmp = new SKBitmap(640, 480);
            using var canvas = new SKCanvas(bmp);
            for (int i = 0; i < 40; i++)
            {
                obj.Render(canvas, transform);
            }
        });

        Task disposeTask = Task.Run(() => obj.Dispose());
        await Task.WhenAll(renderTask, disposeTask);
        obj.Dispose(); // Double-dispose safe
    }

    [Fact]
    public void TranslateBars_AndLockRange_PreserveBarCountAndRespectAfterMoveFrameExclusion()
    {
        var candles = MakeWavyCandles(40);
        using var obj = new DistributionRangeObject(
            new ChartPoint(candles[5].Timestamp, 100m),
            new ChartPoint(candles[15].Timestamp, 110m))
        {
            LockRange = true
        };
        obj.Recalculate(candles);
        Assert.Equal(11, obj.RangeBars);
        Assert.True(BarLockedTranslation.IsBarLocked(obj));

        Assert.True(BarLockedTranslation.TryTranslate(obj, candles[5].Timestamp, candles[8].Timestamp, candles));
        Assert.Equal(candles[8].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[18].Timestamp, obj.Points[1].Time);

        // AfterMoveFrame excludes heavy DistributionRangeObject recomputation
        decimal? beforeMean = obj.CachedResult.Mean;
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Equal(beforeMean, obj.CachedResult.Mean);

        // AfterMoveCompleted recalculates
        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.Equal(11, obj.RangeBars);
        Assert.NotEqual(beforeMean, obj.CachedResult.Mean);
    }

    [Fact]
    public void DrawingDocumentCodec_RoundTrip_PreservesAllDistributionRangeProperties()
    {
        var candles = MakeWavyCandles(40);
        var manager = new ChartObjectManager();
        var dist = new DistributionRangeObject(
            new ChartPoint(candles[3].Timestamp, candles[3].Close),
            new ChartPoint(candles[28].Timestamp, candles[28].Close))
        {
            DataMode = DistributionDataMode.Price,
            PriceSource = PriceType.Weighted,
            DisplayStyle = DistributionDisplayStyle.HorizontalPriceProfile,
            BinningRule = DistributionBinningRule.FixedCount,
            FixedBinCount = 14,
            ReferenceDistribution = ReferenceDistributionType.Gev,
            OverrideStudentTDegreesOfFreedom = true,
            StudentTDegreesOfFreedom = 7.5m,
            GevShapeParameter = -0.25m,
            BandwidthMultiplier = 1.5,
            IncludeKde = false,
            ShowStatsLabel = false,
            PlotHeightRatio = 0.60,
            RightToLeft = true,
            LockRange = true,
            FillOpacity = 25,
            BarOpacity = 0.55,
            BarColor = Color.Parse("#FF5722"),
            KdeColor = Color.Parse("#4CAF50"),
            ReferenceCurveColor = Color.Parse("#9C27B0")
        };
        dist.Recalculate(candles);
        manager.AddObject(dist);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("DIST", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        DrawingDocumentState restoredState = DrawingDocumentCodec.DeserializeFromJson(json);
        using var materialized = DrawingDocumentCodec.Materialize(restoredState);

        var restored = Assert.IsType<DistributionRangeObject>(
            Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));

        Assert.Equal(ChartObjectType.DistributionRange, restored.Type);
        Assert.Equal(DistributionDataMode.Price, restored.DataMode);
        Assert.Equal(PriceType.Weighted, restored.PriceSource);
        Assert.Equal(DistributionDisplayStyle.HorizontalPriceProfile, restored.DisplayStyle);
        Assert.Equal(DistributionBinningRule.FixedCount, restored.BinningRule);
        Assert.Equal(14, restored.FixedBinCount);
        Assert.Equal(ReferenceDistributionType.Gev, restored.ReferenceDistribution);
        Assert.True(restored.OverrideStudentTDegreesOfFreedom);
        Assert.Equal(7.5m, restored.StudentTDegreesOfFreedom);
        Assert.Equal(-0.25m, restored.GevShapeParameter);
        Assert.Equal(1.5, restored.BandwidthMultiplier);
        Assert.False(restored.IncludeKde);
        Assert.False(restored.ShowStatsLabel);
        Assert.Equal(0.60, restored.PlotHeightRatio);
        Assert.True(restored.RightToLeft);
        Assert.True(restored.LockRange);
        Assert.Equal(25, restored.FillOpacity);
        Assert.Equal(0.55, restored.BarOpacity);
        Assert.Equal(Color.Parse("#FF5722"), restored.BarColor);
        Assert.Equal(Color.Parse("#4CAF50"), restored.KdeColor);
        Assert.Equal(Color.Parse("#9C27B0"), restored.ReferenceCurveColor);
    }
}
