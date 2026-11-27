using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public sealed class GarchVolatilityConeObjectTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>A path with volatility clustering from a small deterministic generator.</summary>
    private static List<CoreCandleData> MakeCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        double state = 4242.0;
        double variance = 1.0;
        decimal close = 100m;
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                state = (state * 1103515245.0 + 12345.0) % 2147483648.0;
                double shock = (state / 2147483648.0 - 0.5) * 3.4641016151377544;
                double ret = Math.Sqrt(variance) * shock;
                variance = 0.2 + 0.1 * ret * ret + 0.8 * variance;
                close = Math.Round(close * (decimal)Math.Exp(ret * 0.01), 6);
            }

            list.Add(new CoreCandleData(BaseTime.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 60m, 160m, width, height);

    private static GarchVolatilityConeObject Create(List<CoreCandleData> candles, int first = 20, int last = 100)
        => new(new ChartPoint(candles[first].Timestamp, candles[first].Close), new ChartPoint(candles[last].Timestamp, candles[last].Close));

    private static void RenderOnce(GarchVolatilityConeObject obj, int width = 900, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.ClipRect(new SKRect(0, 0, width, height));
        obj.Render(canvas, MakeTransform(width, height));
    }

    [Fact]
    public void Registration_ToolBehaviorCategoryAndEnums()
    {
        IDrawingToolBehavior? behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.GarchVolatilityCone);
        Assert.NotNull(behavior);
        Assert.Equal(2, behavior!.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var algorithms = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Algorithms");
        Assert.Contains(algorithms.Tools, t => t.Tool == DrawingTool.GarchVolatilityCone && t.NameKey == "DrawTool_GarchVolatilityCone");

        // Append-only enums: the new member directly follows the previous last one (later tools are appended after it).
        Assert.Equal(ChartObjectType.DistributionRange + 1, ChartObjectType.GarchVolatilityCone);
        Assert.Equal(DrawingTool.DistributionRange + 1, DrawingTool.GarchVolatilityCone);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolAndItsParameters(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        foreach (string key in new[] { "DrawTool_GarchVolatilityCone", "Param_ForecastBars", "Param_ShowInnerBand", "Param_ShowOuterBand", "Param_FillOpacity" })
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Fact]
    public void Behavior_CreatesTwoAnchorsAndDoesNotCalculateWhilePlacing()
    {
        var candles = MakeCandles(150);
        var behavior = new GarchVolatilityConeBehavior();
        var first = new ChartPoint(candles[20].Timestamp, candles[20].Close);

        using var obj = (GarchVolatilityConeObject)behavior.CreateObject(first, candles);
        behavior.UpdatePoint(obj, 1, new ChartPoint(candles[100].Timestamp, candles[100].Close), candles);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(candles[20].Timestamp, obj.Points[0].Time);
        Assert.Equal(candles[100].Timestamp, obj.Points[1].Time);
        Assert.Equal(GarchConeStatus.NoRange, obj.Cone.Status);   // nothing computed until the placement has finished
        Assert.Empty(obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public void Recalculate_ComputesTheCone_AndTheValuesFollow()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.ForecastBars = 15;

        obj.Recalculate(candles);

        GarchVolatilityCone expected = GarchVolatilityConeValues.Calculate(candles, 20, 100, 15);
        Assert.True(obj.Cone.IsOk);
        Assert.Equal(81, obj.Cone.RangeBars);
        Assert.Equal(expected.Upper2, obj.Cone.Upper2);
        Assert.Equal(expected.Lower1, obj.Cone.Lower1);

        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[100].Timestamp);
        Assert.Equal(GarchVolatilityConeValues.Calculate(expected, IndicatorColor.Gray).Select(v => v.Key), values.Select(v => v.Key));
        Assert.Equal(81, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(81, values.Single(v => v.Key == "EffectiveBars").NumericValue);
        Assert.Same(values, obj.GetCalculatedValues(candles[100].Timestamp));   // cached: no rebuild while nothing changed
    }

    [Fact]
    public void ForecastBars_ChangeIsPickedUpByTheNextRecalculation()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.Recalculate(candles);
        Assert.Equal(GarchVolatilityConeValues.DefaultHorizon, obj.Cone.Horizon);

        obj.ForecastBars = 40;
        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        Assert.Equal(40, obj.Cone.Horizon);
        Assert.Equal(40, obj.Cone.Upper1.Length);
    }

    [Fact]
    public void Dispatcher_AfterMoveFrame_AndAfterMoveCompleted()
    {
        var candles = MakeCandles(160);
        using var obj = Create(candles, 20, 100);
        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));
        GarchVolatilityCone before = obj.Cone;
        Assert.True(before.IsOk);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[100].Timestamp));

        // A move: the points change, the per-frame recalculation does NOT run the heavy fit, the result no longer belongs to the points.
        obj.Translate(TimeSpan.FromDays(10), 0m);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Same(before, obj.Cone);
        Assert.Empty(obj.GetCalculatedValues(candles[110].Timestamp));

        // The end of the move recalculates for the new range.
        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.NotSame(before, obj.Cone);
        Assert.Equal(81, obj.Cone.RangeBars);
        Assert.Equal(GarchVolatilityConeValues.Calculate(candles, 30, 110, GarchVolatilityConeValues.DefaultHorizon).Upper2, obj.Cone.Upper2);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[110].Timestamp));
    }

    [Fact]
    public void ShortRange_ShowsTheStateInsteadOfBands()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles, 40, 60);

        obj.Recalculate(candles);

        Assert.Equal(GarchConeStatus.TooFewBars, obj.Cone.Status);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[60].Timestamp);
        Assert.Equal("State", values[0].Key);
        Assert.Equal("Insufficient bars (< 31)", values[0].FormattedText);
        RenderOnce(obj);   // draws the range edges and the state text
    }

    [Fact]
    public void RangeWithoutBars_HasNoValues_AndRenders()
    {
        var candles = MakeCandles(150);
        using var obj = new GarchVolatilityConeObject(
            new ChartPoint(BaseTime.AddDays(-50), 100m),
            new ChartPoint(BaseTime.AddDays(-30), 100m));

        obj.Recalculate(candles);

        Assert.Equal(GarchConeStatus.NoRange, obj.Cone.Status);
        Assert.Empty(obj.GetCalculatedValues(BaseTime));
        RenderOnce(obj);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Render_AcrossBandSwitches_DoesNotThrow(bool inner, bool outer, bool selected)
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.ShowInnerBand = inner;
        obj.ShowOuterBand = outer;
        obj.IsSelected = selected;
        obj.Recalculate(candles);

        RenderOnce(obj);
        RenderOnce(obj, 300, 40);   // a tiny viewport
    }

    [Fact]
    public void LongRange_ShowsTheTruncationBadge_AndTheCountsInTheValues()
    {
        int total = GarchVolatilityConeValues.MaxBars + 100;
        var candles = MakeCandles(total);
        using var obj = new GarchVolatilityConeObject(
            new ChartPoint(candles[0].Timestamp, candles[0].Close),
            new ChartPoint(candles[total - 1].Timestamp, candles[total - 1].Close));

        obj.Recalculate(candles);

        Assert.True(obj.Cone.IsTruncated);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[total - 1].Timestamp);
        Assert.Equal(total, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(GarchVolatilityConeValues.MaxBars, values.Single(v => v.Key == "EffectiveBars").NumericValue);
    }

    [Fact]
    public void Render_DoesNotAllocateManagedMemory()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);   // warm-up
        long bytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));

        Assert.Equal(0L, bytes);
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.Equal(0L, hitBytes);
    }

    [Fact]
    public void DisposeThenRender_IsASafeNoOp_AndDisposeIsIdempotent()
    {
        var candles = MakeCandles(150);
        var obj = Create(candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));

        obj.Dispose();
        obj.Dispose();
        obj.Render(surface.Canvas, transform);
        obj.Recalculate(candles);

        Assert.False(obj.HitTest(new Point(100, 100), transform));
        Assert.Empty(obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public async Task RenderAndDispose_ConcurrentSafety_DoesNotThrow()
    {
        var candles = MakeCandles(150);
        var obj = Create(candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();

        Task renderTask = Task.Run(() =>
        {
            using var bitmap = new SKBitmap(640, 480);
            using var canvas = new SKCanvas(bitmap);
            for (int i = 0; i < 40; i++)
            {
                obj.Render(canvas, transform);
            }
        });
        Task disposeTask = Task.Run(() => obj.Dispose());

        await Task.WhenAll(renderTask, disposeTask);
        obj.Dispose();
    }

    [Fact]
    public void HitTest_RangeEdges_AndTheInsideOfTheCone()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();

        Point leftEdge = transform.ChartToScreen(obj.Points[0]);
        Point rightEdge = transform.ChartToScreen(obj.Points[1]);
        Assert.True(obj.HitTest(new Point(leftEdge.X, 5), transform, 4.0));
        Assert.True(obj.HitTest(new Point(rightEdge.X + 2, 590), transform, 4.0));
        Assert.False(obj.HitTest(new Point(leftEdge.X + 15, 300), transform, 4.0));   // inside the range, not an edge, not the cone

        // Inside the cone: on the centre line a few bars after the range end.
        GarchVolatilityCone cone = obj.Cone;
        Point inside = transform.ChartToScreen(new ChartPoint(candles[105].Timestamp, cone.CenterPrice));
        Assert.True(obj.HitTest(inside, transform, 4.0));

        // Far above the cone.
        Assert.False(obj.HitTest(new Point(inside.X, 2), transform, 4.0));
    }

    [Fact]
    public void Parameters_AreClampedToTheirRanges()
    {
        using var obj = new GarchVolatilityConeObject();

        obj.ForecastBars = 1;
        Assert.Equal(GarchVolatilityConeValues.MinHorizon, obj.ForecastBars);
        obj.ForecastBars = 500;
        Assert.Equal(GarchVolatilityConeValues.MaxHorizon, obj.ForecastBars);
        obj.FillOpacity = -5;
        Assert.Equal(0, obj.FillOpacity);
        obj.FillOpacity = 300;
        Assert.Equal(100, obj.FillOpacity);
        Assert.Equal(GarchVolatilityConeValues.DefaultHorizon, new GarchVolatilityConeObject().ForecastBars);
    }

    [Fact]
    public void DrawingDocumentCodec_RoundTrip_KeepsTheParametersAndNothingComputed()
    {
        var candles = MakeCandles(150);
        var manager = new ChartObjectManager();
        var cone = Create(candles);
        cone.ForecastBars = 33;
        cone.ShowInnerBand = false;
        cone.FillOpacity = 42;
        cone.Thickness = 2.5;
        cone.Recalculate(candles);
        manager.AddObject(cone);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("GARCH", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        DrawingDocumentState restoredState = DrawingDocumentCodec.DeserializeFromJson(json);
        using var materialized = DrawingDocumentCodec.Materialize(restoredState);

        var restored = Assert.IsType<GarchVolatilityConeObject>(Assert.Single(materialized.Objects[ChartDrawingContextType.Standard]));
        Assert.Equal(ChartObjectType.GarchVolatilityCone, restored.Type);
        Assert.Equal(33, restored.ForecastBars);
        Assert.False(restored.ShowInnerBand);
        Assert.True(restored.ShowOuterBand);
        Assert.Equal(42, restored.FillOpacity);
        Assert.Equal(2.5, restored.Thickness);
        Assert.Equal(candles[20].Timestamp, restored.Points[0].Time);
        Assert.Equal(candles[100].Timestamp, restored.Points[1].Time);
        Assert.Equal(GarchConeStatus.NoRange, restored.Cone.Status);   // the cone is computed again on load, never stored
        Assert.DoesNotContain("upper", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cone", json.Replace("garchVolatilityCone", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);

        restored.Recalculate(candles);
        Assert.Equal(33, restored.Cone.Horizon);
        Assert.True(restored.Cone.IsOk);
    }

    [Fact]
    public void ChartObjectJsonConverter_RoundTrip_KeepsTheParametersAndNothingComputed()
    {
        var candles = MakeCandles(150);
        using var cone = Create(candles);
        cone.ForecastBars = 27;
        cone.ShowOuterBand = false;
        cone.FillOpacity = 7;
        cone.Recalculate(candles);

        string json = JsonSerializer.Serialize<IChartObject>(cone, JsonOptions);
        using var restored = Assert.IsType<GarchVolatilityConeObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));

        Assert.Equal(27, restored.ForecastBars);
        Assert.False(restored.ShowOuterBand);
        Assert.True(restored.ShowInnerBand);
        Assert.Equal(7, restored.FillOpacity);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(candles[20].Timestamp, restored.Points[0].Time);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("cone", StringComparison.OrdinalIgnoreCase) || n.Contains("overlay", StringComparison.OrdinalIgnoreCase) || n.Contains("times", StringComparison.OrdinalIgnoreCase));
    }
}
