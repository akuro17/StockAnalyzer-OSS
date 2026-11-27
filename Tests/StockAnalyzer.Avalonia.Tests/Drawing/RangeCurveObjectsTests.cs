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
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The polynomial and the nonlinear regression drawing tools (DSL expansion run D-2): the range curve family on top of <see cref="RangeCurveObjectBase"/>.</summary>
public sealed class RangeCurveObjectsTests
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

    private static List<CoreCandleData> MakeCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i * 0.2m) + Math.Round((decimal)(Math.Sin(i * 0.25) * 4.0), 4);
            list.Add(new CoreCandleData(BaseTime.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, width, height);

    private static ChartPoint At(List<CoreCandleData> candles, int index) => new(candles[index].Timestamp, candles[index].Close);

    private static void RenderOnce(RangeAnalysisObjectBase obj, int width = 900, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.ClipRect(new SKRect(0, 0, width, height));
        obj.Render(canvas, MakeTransform(width, height));
    }

    public static IEnumerable<object[]> BothTools()
    {
        yield return new object[] { DrawingTool.PolynomialRegressionProjection };
        yield return new object[] { DrawingTool.NonlinearRegressionCurve };
    }

    private static RangeCurveObjectBase Create(DrawingTool tool, List<CoreCandleData> candles, int first = 20, int last = 100)
        => tool == DrawingTool.PolynomialRegressionProjection
            ? new PolynomialRegressionProjectionObject(At(candles, first), At(candles, last))
            : new NonlinearRegressionCurveObject(At(candles, first), At(candles, last));

    [Fact]
    public void Registration_ToolsBehavioursCategoryAndEnums()
    {
        foreach (DrawingTool tool in new[] { DrawingTool.PolynomialRegressionProjection, DrawingTool.NonlinearRegressionCurve })
        {
            IDrawingToolBehavior? behavior = DrawingToolBehaviorRegistry.GetBehavior(tool);
            Assert.NotNull(behavior);
            Assert.Equal(2, behavior!.RequiredSteps);
            Assert.False(behavior.FinishesOnRelease);
            var channels = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Channels");
            Assert.Contains(channels.Tools, t => t.Tool == tool && t.NameKey == $"DrawTool_{tool}");
        }

        // Append-only enums: the two members follow GarchVolatilityCone, in the same order in both enums.
        Assert.Equal(DrawingTool.GarchVolatilityCone + 1, DrawingTool.PolynomialRegressionProjection);
        Assert.Equal(DrawingTool.GarchVolatilityCone + 2, DrawingTool.NonlinearRegressionCurve);
        Assert.Equal(ChartObjectType.GarchVolatilityCone + 1, ChartObjectType.PolynomialRegressionProjection);
        Assert.Equal(ChartObjectType.GarchVolatilityCone + 2, ChartObjectType.NonlinearRegressionCurve);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolsTheirParametersAndTheModelEnum(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        var keys = new List<string>
        {
            "DrawTool_PolynomialRegressionProjection", "DrawTool_NonlinearRegressionCurve",
            "Param_Degree", "Param_ForecastBars", "Param_StdDevMultiplier", "Param_ShowBand", "Param_ModelType", "Param_ExtendBars",
        };
        keys.AddRange(Enum.GetValues<NonlinearRegressionModel>().Select(m => $"Enum_{nameof(NonlinearRegressionModel)}_{m}"));
        foreach (string key in keys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public void Behavior_PlacesTwoAnchors_AndDoesNotCalculateWhilePlacing(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        IDrawingToolBehavior behavior = DrawingToolBehaviorRegistry.GetBehavior(tool)!;

        using var obj = (RangeCurveObjectBase)behavior.CreateObject(At(candles, 20), candles);
        behavior.UpdatePoint(obj, 1, At(candles, 100), candles);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(candles[100].Timestamp, obj.Points[1].Time);
        Assert.Equal(0, obj.Curve.Count);
        Assert.Empty(obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public void Polynomial_RecalculateDrawsTheCurveOfTheCoreCalculation()
    {
        var candles = MakeCandles(150);
        using var obj = new PolynomialRegressionProjectionObject(At(candles, 20), At(candles, 100)) { Degree = 3, ForecastBars = 6, StdDevMultiplier = 1.5m };

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        PolynomialRegressionFit expected = PolynomialRegressionProjectionValues.Calculate(candles, 20, 100, 3, 6, 1.5m);
        Assert.True(obj.Fit.IsOk);
        Assert.Equal(expected.Curve.Prices, obj.Curve.Prices);
        Assert.Equal(expected.Curve.Times, obj.Curve.Times);
        Assert.Equal(81 + 6, obj.Curve.Count);
        Assert.Equal(81, obj.Curve.FittedCount);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[100].Timestamp);
        Assert.Equal(PolynomialRegressionProjectionValues.Calculate(expected, IndicatorColor.Gray).Select(v => v.Key), values.Select(v => v.Key));
        Assert.Same(values, obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public void Nonlinear_RecalculateDrawsTheCurveOfTheCoreCalculation()
    {
        var candles = MakeCandles(150);
        using var obj = new NonlinearRegressionCurveObject(At(candles, 20), At(candles, 100)) { ModelType = NonlinearRegressionModel.Logarithmic, ExtendBars = 15 };

        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));

        NonlinearRegressionFit expected = NonlinearRegressionCurveValues.Calculate(candles, 20, 100, NonlinearRegressionModel.Logarithmic, 15);
        Assert.True(obj.Fit.IsOk);
        Assert.Equal(expected.Curve.Prices, obj.Curve.Prices);
        Assert.Equal(81 + 15, obj.Curve.Count);
        Assert.Equal(expected.ShapeParameter, obj.Fit.ShapeParameter);
        Assert.Equal(81, obj.GetCalculatedValues(candles[100].Timestamp).Single(v => v.Key == "RangeBars").NumericValue);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public void AfterMoveFrame_DoesNotRecalculate_AfterMoveCompletedDoes(DrawingTool tool)
    {
        var candles = MakeCandles(160);
        using var obj = Create(tool, candles);
        obj.Recalculate(candles);
        RangeCurve before = obj.Curve;
        Assert.True(before.Count > 0);

        obj.Translate(TimeSpan.FromDays(12), 0m);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Same(before, obj.Curve);
        Assert.Empty(obj.GetCalculatedValues(candles[110].Timestamp));   // the result no longer belongs to the moved anchors

        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.NotSame(before, obj.Curve);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[110].Timestamp));
    }

    [Fact]
    public void ShortRange_ShowsTheStateInsteadOfACurve()
    {
        var candles = MakeCandles(150);
        using var poly = new PolynomialRegressionProjectionObject(At(candles, 40), At(candles, 42)) { Degree = 4 };
        using var nonlinear = new NonlinearRegressionCurveObject(At(candles, 40), At(candles, 42));

        poly.Recalculate(candles);
        nonlinear.Recalculate(candles);

        Assert.Equal(RangeFitStatus.TooFewBars, poly.Fit.Status);
        Assert.Equal(RangeFitStatus.TooFewBars, nonlinear.Fit.Status);
        Assert.Equal(0, poly.Curve.Count);
        Assert.Equal("State", poly.GetCalculatedValues(candles[42].Timestamp)[0].Key);
        Assert.Equal("Insufficient bars (< 6)", poly.GetCalculatedValues(candles[42].Timestamp)[0].FormattedText);
        Assert.Equal("Insufficient bars (< 5)", nonlinear.GetCalculatedValues(candles[42].Timestamp)[0].FormattedText);
        RenderOnce(poly);
        RenderOnce(nonlinear);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public void RangeWithoutBars_HasNoValues_AndRenders(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        using RangeCurveObjectBase obj = tool == DrawingTool.PolynomialRegressionProjection
            ? new PolynomialRegressionProjectionObject(new ChartPoint(BaseTime.AddDays(-40), 100m), new ChartPoint(BaseTime.AddDays(-20), 100m))
            : new NonlinearRegressionCurveObject(new ChartPoint(BaseTime.AddDays(-40), 100m), new ChartPoint(BaseTime.AddDays(-20), 100m));

        obj.Recalculate(candles);

        Assert.Empty(obj.GetCalculatedValues(BaseTime));
        RenderOnce(obj);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Polynomial_Render_AcrossBandSwitches_DoesNotThrow(bool showBand, bool selected)
    {
        var candles = MakeCandles(150);
        using var obj = new PolynomialRegressionProjectionObject(At(candles, 20), At(candles, 100)) { ShowBand = showBand, ForecastBars = 8, IsSelected = selected };
        obj.Recalculate(candles);

        RenderOnce(obj);
        RenderOnce(obj, 300, 40);
    }

    [Fact]
    public void Nonlinear_Render_AllModelsAndExtension_DoNotThrow()
    {
        var candles = MakeCandles(150);
        foreach (NonlinearRegressionModel model in Enum.GetValues<NonlinearRegressionModel>())
        {
            using var obj = new NonlinearRegressionCurveObject(At(candles, 20), At(candles, 100)) { ModelType = model, ExtendBars = 20, IsSelected = true };
            obj.Recalculate(candles);
            Assert.True(obj.Fit.IsOk, model.ToString());
            RenderOnce(obj);
        }
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public void Render_AndHitTest_DoNotAllocateManagedMemory(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        using var obj = Create(tool, candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);   // warm-up
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));

        Assert.Equal(0L, renderBytes);
        Assert.Equal(0L, hitBytes);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public void DisposeThenRender_IsASafeNoOp_AndConcurrentRenderIsSafe(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        var obj = Create(tool, candles);
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

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task RenderAndDispose_ConcurrentSafety_DoesNotThrow(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        var obj = Create(tool, candles);
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

    [Theory]
    [MemberData(nameof(BothTools))]
    public void HitTest_RangeEdges_TheCurve_AndNotTheEmptySpace(DrawingTool tool)
    {
        var candles = MakeCandles(150);
        using var obj = Create(tool, candles);
        obj.Recalculate(candles);
        var transform = MakeTransform();

        Point leftEdge = transform.ChartToScreen(obj.Points[0]);
        Assert.True(obj.HitTest(new Point(leftEdge.X, 5), transform, 4.0));

        // On the curve in the middle of the range.
        RangeCurve curve = obj.Curve;
        int mid = curve.FittedCount / 2;
        Point onCurve = transform.ChartToScreen(new ChartPoint(curve.Times[mid], curve.Prices[mid]));
        Assert.True(obj.HitTest(onCurve, transform, 4.0));

        // Far above the curve (and its band).
        Assert.False(obj.HitTest(new Point(onCurve.X, 3), transform, 4.0));
    }

    [Fact]
    public void Parameters_AreClampedToTheirRanges()
    {
        using var poly = new PolynomialRegressionProjectionObject();
        poly.Degree = 0;
        Assert.Equal(1, poly.Degree);
        poly.Degree = 50;
        Assert.Equal(6, poly.Degree);
        poly.ForecastBars = -1;
        Assert.Equal(0, poly.ForecastBars);
        poly.ForecastBars = 99;
        Assert.Equal(10, poly.ForecastBars);
        poly.StdDevMultiplier = 0m;
        Assert.Equal(0.1m, poly.StdDevMultiplier);
        poly.StdDevMultiplier = 99m;
        Assert.Equal(10m, poly.StdDevMultiplier);
        Assert.Equal(2, new PolynomialRegressionProjectionObject().Degree);
        Assert.Equal(2.0m, new PolynomialRegressionProjectionObject().StdDevMultiplier);

        using var nonlinear = new NonlinearRegressionCurveObject();
        nonlinear.ExtendBars = -5;
        Assert.Equal(0, nonlinear.ExtendBars);
        nonlinear.ExtendBars = 500;
        Assert.Equal(50, nonlinear.ExtendBars);
    }

    [Fact]
    public void DrawingDocumentCodec_RoundTrip_KeepsTheParametersOfBothTools()
    {
        var candles = MakeCandles(150);
        var manager = new ChartObjectManager();
        var poly = new PolynomialRegressionProjectionObject(At(candles, 20), At(candles, 100)) { Degree = 4, ForecastBars = 7, StdDevMultiplier = 1.25m, ShowBand = false, Thickness = 2.0 };
        var nonlinear = new NonlinearRegressionCurveObject(At(candles, 30), At(candles, 110)) { ModelType = NonlinearRegressionModel.Logistic, ExtendBars = 33 };
        poly.Recalculate(candles);
        nonlinear.Recalculate(candles);
        manager.AddObject(poly);
        manager.AddObject(nonlinear);

        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("RNG", TimeframeType.Daily), revision: 1L);
        string json = DrawingDocumentCodec.SerializeToJson(state);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(json));

        var objects = materialized.Objects[ChartDrawingContextType.Standard];
        var restoredPoly = Assert.IsType<PolynomialRegressionProjectionObject>(objects.Single(o => o is PolynomialRegressionProjectionObject));
        var restoredNonlinear = Assert.IsType<NonlinearRegressionCurveObject>(objects.Single(o => o is NonlinearRegressionCurveObject));

        Assert.Equal(4, restoredPoly.Degree);
        Assert.Equal(7, restoredPoly.ForecastBars);
        Assert.Equal(1.25m, restoredPoly.StdDevMultiplier);
        Assert.False(restoredPoly.ShowBand);
        Assert.Equal(2.0, restoredPoly.Thickness);
        Assert.Equal(candles[100].Timestamp, restoredPoly.Points[1].Time);
        Assert.Equal(NonlinearRegressionModel.Logistic, restoredNonlinear.ModelType);
        Assert.Equal(33, restoredNonlinear.ExtendBars);
        Assert.Equal(0, restoredPoly.Curve.Count);   // computed again on load, never stored
        Assert.DoesNotContain("prices", json, StringComparison.OrdinalIgnoreCase);

        restoredPoly.Recalculate(candles);
        restoredNonlinear.Recalculate(candles);
        Assert.True(restoredPoly.Fit.IsOk);
        Assert.True(restoredNonlinear.Fit.IsOk);
    }

    [Fact]
    public void ChartObjectJsonConverter_RoundTrip_KeepsTheParametersAndNothingComputed()
    {
        var candles = MakeCandles(150);
        using var nonlinear = new NonlinearRegressionCurveObject(At(candles, 20), At(candles, 100)) { ModelType = NonlinearRegressionModel.Power, ExtendBars = 9 };
        nonlinear.Recalculate(candles);

        string json = JsonSerializer.Serialize<IChartObject>(nonlinear, JsonOptions);
        using var restored = Assert.IsType<NonlinearRegressionCurveObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));

        Assert.Equal(NonlinearRegressionModel.Power, restored.ModelType);
        Assert.Equal(9, restored.ExtendBars);
        Assert.Equal(2, restored.Points.Count);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("curve", StringComparison.OrdinalIgnoreCase) || n.Contains("fit", StringComparison.OrdinalIgnoreCase) || n.Contains("overlay", StringComparison.OrdinalIgnoreCase));
    }
}
