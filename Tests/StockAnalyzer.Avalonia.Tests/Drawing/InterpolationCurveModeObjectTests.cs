using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The curve mode of PCHIP Curve and Cubic Spline Curve in the chart objects (settings follow-up Run 8): defaults, the placement behaviour, recalculation, persistence on both paths and rendering of a loop.</summary>
public sealed class InterpolationCurveModeObjectTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static List<CoreCandleData> Bars(int count = 120)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CoreCandleData(Start.AddDays(i), 100m, 101m, 99m, 100m, 1000L));
        }

        return list;
    }

    private static ChartPoint P(int bar, decimal price) => new(Start.AddDays(bar), price);

    public static TheoryData<string> Tools() => new() { "Pchip", "CubicSpline" };

    private static InterpolationCurveObjectBase Create(string tool, params ChartPoint[] points)
        => tool == "Pchip" ? new PchipCurveObject(points.ToList()) : new CubicSplineCurveObject(points.ToList());

    private static IDrawingToolBehavior BehaviorOf(string tool) => tool == "Pchip" ? new PchipCurveBehavior() : new CubicSplineCurveBehavior();

    [Theory]
    [MemberData(nameof(Tools))]
    public void TheClassDefaultIsTimeSeries_AndANewlyPlacedCurveIsAPath(string tool)
    {
        using InterpolationCurveObjectBase bare = Create(tool);
        Assert.Equal(InterpolationCurveMode.TimeSeries, bare.CurveMode);   // what a file without the setting keeps

        var created = (InterpolationCurveObjectBase)BehaviorOf(tool).CreateObject(P(10, 100m));
        try
        {
            Assert.Equal(InterpolationCurveMode.Path, created.CurveMode);
        }
        finally
        {
            created.Dispose();
        }
    }

    [Theory]
    [InlineData(DrawingTool.PchipCurve)]
    [InlineData(DrawingTool.CubicSplineCurve)]
    public void TheToolPreset_StartsAsAPath_SoTheNewObjectKeepsIt(DrawingTool tool)
    {
        IChartObject? prototype = new DrawingToolPresetService().GetOrCreatePresetObject(tool);
        try
        {
            var curve = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(prototype);
            Assert.Equal(InterpolationCurveMode.Path, curve.CurveMode);
        }
        finally
        {
            (prototype as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ThePlacementBehaviour_KeepsThePathMode_WhileThePreviewAnchorFollowsThePointer(string tool)
    {
        IDrawingToolBehavior behavior = BehaviorOf(tool);
        var curve = (InterpolationCurveObjectBase)behavior.CreateObject(P(10, 100m));
        try
        {
            behavior.UpdatePoint(curve, 0, P(30, 120m));
            Assert.Equal(2, curve.Points.Count);
            Assert.Equal(Start.AddDays(30), curve.Points[1].Time);
            Assert.Equal(InterpolationCurveMode.Path, curve.CurveMode);
        }
        finally
        {
            curve.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ABackwardAnchor_MakesAPathCurveTurnBack_AndTheTimeSeriesModeSortsIt(string tool)
    {
        List<CoreCandleData> candles = Bars();
        using InterpolationCurveObjectBase curve = Create(tool, P(10, 100m), P(40, 130m), P(25, 150m));
        curve.CurveMode = InterpolationCurveMode.Path;
        curve.Recalculate(candles);

        InterpolationCurve path = curve.Curve;
        Assert.True(path.IsOk);
        Assert.Contains(Enumerable.Range(1, path.Count - 1), i => path.Times[i] < path.Times[i - 1]);
        Assert.NotEmpty(curve.GetCalculatedValues(Start));

        // Changing the mode makes the cached curve stale (the straight polyline is drawn) until the recalculation that the settings dialog and the chart run.
        curve.CurveMode = InterpolationCurveMode.TimeSeries;
        Assert.Empty(curve.GetCalculatedValues(Start));
        curve.Recalculate(candles);
        InterpolationCurve sorted = curve.Curve;
        Assert.True(sorted.IsOk);
        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.True(sorted.Times[i] >= sorted.Times[i - 1]);
        }

        Assert.NotEmpty(curve.GetCalculatedValues(Start));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void ALoopRendersWithoutError_SelectedOrNot_AndIsHitOnItsOutline(string tool)
    {
        List<CoreCandleData> candles = Bars();
        using InterpolationCurveObjectBase curve = Create(tool, P(20, 100m), P(50, 100m), P(50, 130m), P(20, 130m), P(20, 100m));
        curve.CurveMode = InterpolationCurveMode.Path;
        curve.Recalculate(candles);
        var transform = new LinearCoordinateTransform(Start, Start.AddDays(120), 80m, 150m, 900, 600);

        foreach (bool selected in new[] { false, true })
        {
            curve.IsSelected = selected;
            using var bitmap = new SKBitmap(900, 600);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            curve.Render(canvas, transform);
            Assert.Contains(bitmap.Pixels, p => p != SKColors.Black);
        }

        // The start of the loop lies on the curve; a point far from every part of it does not.
        global::Avalonia.Point onCurve = transform.ChartToScreen(P(20, 100m));
        Assert.True(curve.HitTest(onCurve, transform, 4.0));
        Assert.False(curve.HitTest(new global::Avalonia.Point(5, 5), transform, 4.0));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void TheMode_SurvivesBothPersistencePaths_AndAFileWithoutItIsATimeSeries(string tool)
    {
        List<CoreCandleData> candles = Bars();
        using InterpolationCurveObjectBase curve = Create(tool, P(10, 100m), P(40, 130m), P(25, 150m));
        curve.CurveMode = InterpolationCurveMode.Path;

        string json = JsonSerializer.Serialize<IChartObject>(curve, JsonOptions);
        using var restored = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.Equal(InterpolationCurveMode.Path, restored.CurveMode);

        var manager = new ChartObjectManager();
        manager.AddObject(curve);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("CURVE", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        var codecRestored = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(materialized.Objects[ChartDrawingContextType.Standard].Single());
        Assert.Equal(InterpolationCurveMode.Path, codecRestored.CurveMode);
        codecRestored.Recalculate(candles);
        Assert.True(codecRestored.Curve.IsOk);

        // A file saved before the setting existed has no key: the curve is a time series, its look does not change.
        JsonNode legacy = JsonNode.Parse(json)!;
        legacy.AsObject().Remove("curveMode");
        using var old = Assert.IsAssignableFrom<InterpolationCurveObjectBase>(JsonSerializer.Deserialize<IChartObject>(legacy.ToJsonString(), JsonOptions));
        Assert.Equal(InterpolationCurveMode.TimeSeries, old.CurveMode);
    }
}
