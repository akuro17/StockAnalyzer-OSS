using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using global::Avalonia;
using AvaloniaPoint = global::Avalonia.Point;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public sealed class SpiralDrawingToolsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public void Constructors_UseSpecifiedDefaultsAndSeparateTypes()
    {
        var golden = new GoldenSpiralObject();
        var logarithmic = new LogarithmicSpiralObject();
        var archimedean = new ArchimedeanSpiralObject();

        Assert.Equal(ChartObjectType.GoldenSpiral, golden.Type);
        Assert.Equal(ChartObjectType.LogarithmicSpiral, logarithmic.Type);
        Assert.Equal(ChartObjectType.ArchimedeanSpiral, archimedean.Type);
        Assert.Equal(SpiralMath.DefaultMaxTurns, golden.MaxTurns);
        Assert.Equal(SpiralMath.DefaultLogarithmicGrowthRate, logarithmic.GrowthRate);
        Assert.Equal(SpiralMath.DefaultArchimedeanPitchPixels, archimedean.PitchPixels);
    }

    [Fact]
    public void LogarithmicGrowthRate_ZeroWhenDegenerationDisallowed_ThrowsAndRetainsValue()
    {
        var spiral = new LogarithmicSpiralObject();

        Assert.Throws<ArgumentOutOfRangeException>(() => spiral.GrowthRate = 0.0);
        Assert.Equal(SpiralMath.DefaultLogarithmicGrowthRate, spiral.GrowthRate);
    }

    [Fact]
    public void NumericParameterAdapter_RejectedGrowthRate_RetainsModelValueAndClearsAfterValidRetry()
    {
        var spiral = new LogarithmicSpiralObject();
        var property = typeof(LogarithmicSpiralObject).GetProperty(nameof(LogarithmicSpiralObject.GrowthRate));
        var adapter = new DrawingParameterViewBuilder.NumericParameterAdapter(
            spiral,
            Assert.IsAssignableFrom<System.Reflection.PropertyInfo>(property),
            "Growth Rate is invalid.");

        adapter.Value = 0m;

        Assert.Equal(SpiralMath.DefaultLogarithmicGrowthRate, spiral.GrowthRate);
        Assert.Equal((decimal)SpiralMath.DefaultLogarithmicGrowthRate, adapter.Value);
        Assert.True(adapter.HasErrors);
        Assert.Equal("Growth Rate is invalid.", adapter.ErrorMessage);

        adapter.Value = 0.2m;

        Assert.Equal(0.2, spiral.GrowthRate);
        Assert.False(adapter.HasErrors);
        Assert.Null(adapter.ErrorMessage);
    }

    [Fact]
    public void LogarithmicGrowthRate_ZeroWhenDegenerationAllowed_DrawsCircle()
    {
        var spiral = new LogarithmicSpiralObject
        {
            AllowDegenerateCurve = true,
            GrowthRate = 0.0
        };

        Assert.True(SpiralMath.HitTestLogarithmic(new SKPoint(0f, 10f), new SKPoint(0f, 0f), new SKPoint(10f, 0f), spiral.Direction, spiral.GrowthRate, spiral.MaxTurns, BezierSplineMath.DefaultMaxRadius, 0.1));
    }

    [Fact]
    public void ArchimedeanPitch_Zero_DrawsCircle()
    {
        var spiral = new ArchimedeanSpiralObject { PitchPixels = 0.0 };

        Assert.True(SpiralMath.HitTestArchimedean(new SKPoint(0f, -10f), new SKPoint(0f, 0f), new SKPoint(10f, 0f), spiral.Direction, spiral.PitchPixels, spiral.MaxTurns, BezierSplineMath.DefaultMaxRadius, 0.1));
    }

    [Fact]
    public void SpiralMath_DirectionChangesQuarterTurnScreenYAxis()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);
        double radius = 10.0 * Math.Exp(SpiralMath.DefaultLogarithmicGrowthRate * Math.PI / 2.0);

        Assert.True(SpiralMath.HitTestLogarithmic(new SKPoint(0f, (float)radius), center, start, SpiralDirection.Clockwise, SpiralMath.DefaultLogarithmicGrowthRate, 1.0, BezierSplineMath.DefaultMaxRadius, 0.1));
        Assert.True(SpiralMath.HitTestLogarithmic(new SKPoint(0f, (float)-radius), center, start, SpiralDirection.CounterClockwise, SpiralMath.DefaultLogarithmicGrowthRate, 1.0, BezierSplineMath.DefaultMaxRadius, 0.1));
    }

    [Fact]
    public void SpiralMath_FormulaEndpointsMatchGoldenLogarithmicAndArchimedeanDefinitions()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);
        double goldenRadius = 10.0 * BezierSplineMath.GoldenRatioPhi;
        double logarithmicRadius = 10.0 * Math.Exp(SpiralMath.DefaultLogarithmicGrowthRate * Math.PI / 2.0);
        double archimedeanRadius = 10.0 + SpiralMath.DefaultArchimedeanPitchPixels;

        Assert.True(SpiralMath.HitTestLogarithmic(new SKPoint(0f, (float)goldenRadius), center, start, SpiralDirection.Clockwise, Math.Log(BezierSplineMath.GoldenRatioPhi) / (Math.PI / 2.0), 1.0, BezierSplineMath.DefaultMaxRadius, 0.1));
        Assert.True(SpiralMath.HitTestLogarithmic(new SKPoint(0f, (float)logarithmicRadius), center, start, SpiralDirection.Clockwise, SpiralMath.DefaultLogarithmicGrowthRate, 1.0, BezierSplineMath.DefaultMaxRadius, 0.1));
        Assert.True(SpiralMath.HitTestArchimedean(new SKPoint((float)archimedeanRadius, 0f), center, start, SpiralDirection.Clockwise, SpiralMath.DefaultArchimedeanPitchPixels, 1.0, BezierSplineMath.DefaultMaxRadius, 0.1));
    }

    [Fact]
    public void SpiralMath_HitTestUsesTheRenderedCubicCurve()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(100f, 0f);
        const double growthRate = 0.5;
        SKPoint pointOnFirstCubic = GetFirstLogarithmicCubicMidpoint(center, start, growthRate);

        Assert.True(SpiralMath.HitTestLogarithmic(pointOnFirstCubic, center, start, SpiralDirection.Clockwise, growthRate, 1.0, BezierSplineMath.DefaultMaxRadius, 0.001));
    }

    [Fact]
    public void SpiralMath_InvalidOrDegenerateGeometry_DoesNotHit()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);

        Assert.False(SpiralMath.HitTestLogarithmic(new SKPoint(float.NaN, 0f), center, start, SpiralDirection.Clockwise, 0.1, 1.0, BezierSplineMath.DefaultMaxRadius, 1.0));
        Assert.False(SpiralMath.HitTestArchimedean(new SKPoint(0f, 0f), center, center, SpiralDirection.Clockwise, 50.0, 1.0, BezierSplineMath.DefaultMaxRadius, 1.0));
        Assert.False(SpiralMath.HitTestLogarithmic(start, center, start, SpiralDirection.Clockwise, 0.1, double.PositiveInfinity, BezierSplineMath.DefaultMaxRadius, 1.0));
    }

    [Fact]
    public void SpiralDirection_UndefinedValue_IsRejectedAtObjectAndMathBoundaries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoldenSpiralObject { Direction = (SpiralDirection)99 });
        Assert.False(SpiralMath.HitTestLogarithmic(new SKPoint(10f, 0f), new SKPoint(0f, 0f), new SKPoint(10f, 0f), (SpiralDirection)99, 0.1, 1.0, BezierSplineMath.DefaultMaxRadius, 1.0));
    }

    [Fact]
    public void BezierSpiral_UndefinedDirection_IsRejectedAtSharedApiBoundary()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);
        using var path = new SKPath();

        BezierSplineMath.BuildLogarithmicSpiralPath(path, center, start, direction: (SpiralDirection)99);

        Assert.True(path.IsEmpty);
        Assert.False(BezierSplineMath.HitTestLogarithmicSpiral(start, center, start, direction: (SpiralDirection)99));
    }

    [Fact]
    public void ArchimedeanSpiral_RadiusCapWithinSegment_TerminatesAtCap()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);
        const float maxRadius = 34f;
        const double pitchPixels = 50.0;
        using var path = new SKPath();

        SpiralMath.BuildArchimedeanPath(path, center, start, SpiralDirection.Clockwise, pitchPixels, 1.0, maxRadius);

        SKPoint lastPoint = path.LastPoint;
        double finalRadius = Math.Sqrt(lastPoint.X * lastPoint.X + lastPoint.Y * lastPoint.Y);
        Assert.InRange(finalRadius, maxRadius - 0.001, maxRadius + 0.001);
        Assert.True(SpiralMath.HitTestArchimedean(lastPoint, center, start, SpiralDirection.Clockwise, pitchPixels, 1.0, maxRadius, 0.001));
    }

    [Fact]
    public void Registry_ContainsThreeSeparateTwoClickBehaviors()
    {
        Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.GoldenSpiral)!.RequiredSteps);
        Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.LogarithmicSpiral)!.RequiredSteps);
        Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ArchimedeanSpiral)!.RequiredSteps);
    }

    [Fact]
    public void CategoryService_PlacesOnlyNewSpiralsInShapes()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var shapes = Assert.Single(categories, category => category.NameKey == "DrawCat_Shapes");
        var fibonacci = Assert.Single(categories, category => category.NameKey == "DrawCat_Fibonacci");

        Assert.Contains(shapes.Tools, tool => tool.Tool == DrawingTool.GoldenSpiral);
        Assert.Contains(shapes.Tools, tool => tool.Tool == DrawingTool.LogarithmicSpiral);
        Assert.Contains(shapes.Tools, tool => tool.Tool == DrawingTool.ArchimedeanSpiral);
        Assert.DoesNotContain(fibonacci.Tools, tool => tool.Tool == DrawingTool.GoldenSpiral);
        Assert.DoesNotContain(fibonacci.Tools, tool => tool.Tool == DrawingTool.LogarithmicSpiral);
        Assert.DoesNotContain(fibonacci.Tools, tool => tool.Tool == DrawingTool.ArchimedeanSpiral);
        Assert.Contains(fibonacci.Tools, tool => tool.Tool == DrawingTool.FibonacciSpiral);
    }

    [Fact]
    public void SpiralSettingsPanelDefinition_HandlesOnlySpiralObjects()
    {
        var definition = new SpiralSettingsPanelDefinition();

        Assert.True(definition.CanHandle(new GoldenSpiralObject()));
        Assert.True(definition.CanHandle(new LogarithmicSpiralObject()));
        Assert.True(definition.CanHandle(new ArchimedeanSpiralObject()));
        Assert.True(definition.CanHandle(new FibonacciSpiralObject()));
    }

    [Fact]
    public void FibonacciSpiral_DefaultSafetySettings_PreserveExistingGeometry()
    {
        var spiral = new FibonacciSpiralObject();

        Assert.Equal(SpiralDirection.Clockwise, spiral.Direction);
        Assert.Equal(FibonacciSpiralObject.DefaultMaxTurns, spiral.MaxTurns);
        Assert.Throws<ArgumentOutOfRangeException>(() => spiral.MaxTurns = FibonacciSpiralObject.MaximumTurns + 1.0);
    }

    [Fact]
    public void FibonacciSpiral_CounterClockwise_HitsCounterClockwiseFirstQuadrant()
    {
        double radius = 10.0 * BezierSplineMath.GoldenRatioPhi;

        Assert.True(BezierSplineMath.HitTestLogarithmicSpiral(
            new SKPoint(0f, (float)-radius), new SKPoint(0f, 0f), new SKPoint(10f, 0f), 0.1,
            quadrantCount: 1, maxRadius: BezierSplineMath.DefaultMaxRadius, direction: SpiralDirection.CounterClockwise));
    }

    [Fact]
    public void FibonacciSpiral_DefaultOptionalArguments_PreserveTheOriginalClockwiseFourTurnPath()
    {
        var center = new SKPoint(0f, 0f);
        var start = new SKPoint(10f, 0f);
        var pointOnFirstQuadrant = new SKPoint(0f, (float)(10.0 * BezierSplineMath.GoldenRatioPhi));

        Assert.True(BezierSplineMath.HitTestLogarithmicSpiral(pointOnFirstQuadrant, center, start, 0.1));
        Assert.True(BezierSplineMath.HitTestLogarithmicSpiral(pointOnFirstQuadrant, center, start, 0.1, FibonacciSpiralObject.DefaultQuadrants, FibonacciSpiralObject.DefaultMaxRadius, SpiralDirection.Clockwise));
    }

    [Fact]
    public void SpiralObjects_RoundTripTheirPersistedSettings()
    {
        var logarithmic = new LogarithmicSpiralObject
        {
            Direction = SpiralDirection.CounterClockwise,
            MaxTurns = 6.0,
            AllowDegenerateCurve = true,
            GrowthRate = 0.0
        };
        var archimedean = new ArchimedeanSpiralObject
        {
            Direction = SpiralDirection.CounterClockwise,
            MaxTurns = 7.0,
            PitchPixels = 125.0
        };
        var golden = new GoldenSpiralObject
        {
            Direction = SpiralDirection.CounterClockwise,
            MaxTurns = 8.0
        };
        var fibonacci = new FibonacciSpiralObject
        {
            Direction = SpiralDirection.CounterClockwise,
            MaxTurns = 9.0
        };

        var restoredLogarithmic = RoundTrip<LogarithmicSpiralObject>(logarithmic);
        var restoredArchimedean = RoundTrip<ArchimedeanSpiralObject>(archimedean);
        var restoredGolden = RoundTrip<GoldenSpiralObject>(golden);
        var restoredFibonacci = RoundTrip<FibonacciSpiralObject>(fibonacci);

        Assert.True(restoredLogarithmic.AllowDegenerateCurve);
        Assert.Equal(0.0, restoredLogarithmic.GrowthRate);
        Assert.Equal(logarithmic.Direction, restoredLogarithmic.Direction);
        Assert.Equal(logarithmic.MaxTurns, restoredLogarithmic.MaxTurns);
        Assert.Equal(archimedean.PitchPixels, restoredArchimedean.PitchPixels);
        Assert.Equal(archimedean.Direction, restoredArchimedean.Direction);
        Assert.Equal(archimedean.MaxTurns, restoredArchimedean.MaxTurns);
        Assert.Equal(golden.Direction, restoredGolden.Direction);
        Assert.Equal(golden.MaxTurns, restoredGolden.MaxTurns);
        Assert.Equal(fibonacci.Direction, restoredFibonacci.Direction);
        Assert.Equal(fibonacci.MaxTurns, restoredFibonacci.MaxTurns);
    }

    [Fact]
    public void Render_ValidTwoPointSpirals_DoNotThrow()
    {
        var transform = new IdentityTransform();
        var center = transform.ScreenToChart(new AvaloniaPoint(100, 100));
        var start = transform.ScreenToChart(new AvaloniaPoint(150, 100));
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        using var golden = new GoldenSpiralObject(center, start);
        using var logarithmic = new LogarithmicSpiralObject(center, start);
        using var archimedean = new ArchimedeanSpiralObject(center, start);

        golden.Render(surface.Canvas, transform);
        logarithmic.Render(surface.Canvas, transform);
        archimedean.Render(surface.Canvas, transform);
    }

    private sealed class IdentityTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;
        public AvaloniaPoint ChartToScreen(ChartPoint point) => new((point.Time - DateTime.UnixEpoch).TotalDays, (double)point.Price);
        public ChartPoint ScreenToChart(AvaloniaPoint point) => new(DateTime.UnixEpoch.AddDays(point.X), (decimal)point.Y);
        public AvaloniaPoint NumericToScreen(double x, double y) => new(x, y);
        public (double x, double y) ScreenToNumeric(AvaloniaPoint point) => (point.X, point.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => (double)price;
    }

    private static T RoundTrip<T>(T original) where T : IChartObject
    {
        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        return Assert.IsType<T>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
    }

    private static SKPoint GetFirstLogarithmicCubicMidpoint(SKPoint center, SKPoint start, double growthRate)
    {
        double segmentAngle = MathConstants.TwoPi / 16.0;
        double initialRadius = 100.0;
        double endRadius = initialRadius * Math.Exp(growthRate * segmentAngle);
        float controlScale = (float)(segmentAngle / 3.0);
        var startDerivative = new SKPoint((float)(growthRate * initialRadius), (float)initialRadius);
        double cosine = Math.Cos(segmentAngle);
        double sine = Math.Sin(segmentAngle);
        var endPoint = new SKPoint((float)(endRadius * cosine), (float)(endRadius * sine));
        var endDerivative = new SKPoint(
            (float)(growthRate * endRadius * cosine - endRadius * sine),
            (float)(growthRate * endRadius * sine + endRadius * cosine));
        var control1 = new SKPoint(start.X + startDerivative.X * controlScale, start.Y + startDerivative.Y * controlScale);
        var control2 = new SKPoint(endPoint.X - endDerivative.X * controlScale, endPoint.Y - endDerivative.Y * controlScale);

        return new SKPoint(
            (start.X + 3f * control1.X + 3f * control2.X + endPoint.X) / 8f,
            (start.Y + 3f * control1.Y + 3f * control2.Y + endPoint.Y) / 8f);
    }
}
