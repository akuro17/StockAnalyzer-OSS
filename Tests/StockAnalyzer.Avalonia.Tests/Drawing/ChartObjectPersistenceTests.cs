using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Round-trip tests for the generic IChartObject persistence converter (Task 1 of the
/// chart-drawing-persistence feature). Covers the three constructor "shapes" found across the
/// ~40 drawing-tool classes: a two-ChartPoint constructor with a base-class get-only Points list
/// (TrendLineObject), a List&lt;ChartPoint&gt;-taking constructor with a private-set Points
/// property (PolylineObject), and a constructor with a non-ChartPoint trailing parameter (bool)
/// that drives a private-set discriminator property (LongShortPositionObject).
/// </summary>
public class ChartObjectPersistenceTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private static T RoundTrip<T>(T original) where T : IChartObject
    {
        var json = JsonSerializer.Serialize<IChartObject>(original, Options);
        var restored = JsonSerializer.Deserialize<IChartObject>(json, Options);
        return Assert.IsType<T>(restored);
    }

    [Fact]
    public void TrendLineObject_RoundTrips_PointsColorAndTypeSpecificProperties()
    {
        var original = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 100m),
            new ChartPoint(new DateTime(2024, 2, 1), 120m))
        {
            Color = Colors.Blue,
            Thickness = 2.5,
            ShowProjection = true,
            ProjectionColumns = 7,
            ZIndex = 3
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.TrendLine, restored.Type);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(original.Points[0].Time, restored.Points[0].Time);
        Assert.Equal(original.Points[0].Price, restored.Points[0].Price);
        Assert.Equal(original.Points[1].Time, restored.Points[1].Time);
        Assert.Equal(original.Points[1].Price, restored.Points[1].Price);
        Assert.Equal(Colors.Blue, restored.Color);
        Assert.Equal(2.5, restored.Thickness);
        Assert.Equal(3, restored.ZIndex);
        Assert.True(restored.ShowProjection);
        Assert.Equal(7, restored.ProjectionColumns);
    }

    [Fact]
    public void PolylineObject_RoundTrips_VariableLengthPointList()
    {
        var points = new List<ChartPoint>
        {
            new(new DateTime(2024, 1, 1), 10m),
            new(new DateTime(2024, 1, 2), 11m),
            new(new DateTime(2024, 1, 3), 9m),
            new(new DateTime(2024, 1, 4), 12m),
        };
        var original = new PolylineObject(points)
        {
            Color = Colors.Orange,
            LabelType = PolylineLabelType.Numeric,
            ShowLabels = false,
            FontSize = 14.0
        };

        var restored = RoundTrip(original);

        Assert.Equal(4, restored.Points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].Time, restored.Points[i].Time);
            Assert.Equal(points[i].Price, restored.Points[i].Price);
        }
        Assert.Equal(Colors.Orange, restored.Color);
        Assert.Equal(PolylineLabelType.Numeric, restored.LabelType);
        Assert.False(restored.ShowLabels);
        Assert.Equal(14.0, restored.FontSize);
    }

    [Theory]
    [InlineData(true, ChartObjectType.LongPosition)]
    [InlineData(false, ChartObjectType.ShortPosition)]
    public void LongShortPositionObject_RoundTrips_CtorDrivenDiscriminatorAndExtraProperties(bool isLong, ChartObjectType expectedType)
    {
        var original = new LongShortPositionObject(
            entry: new ChartPoint(new DateTime(2024, 3, 1), 50m),
            stop: new ChartPoint(new DateTime(2024, 3, 1), 45m),
            target: new ChartPoint(new DateTime(2024, 3, 1), 60m),
            isLong: isLong)
        {
            BoxWidth = 33.0,
            AreaOpacity = 0.4
        };

        var restored = RoundTrip(original);

        Assert.Equal(expectedType, restored.Type);
        Assert.Equal(3, restored.Points.Count);
        Assert.Equal(50m, restored.Points[0].Price);
        Assert.Equal(45m, restored.Points[1].Price);
        Assert.Equal(60m, restored.Points[2].Price);
        Assert.Equal(33.0, restored.BoxWidth);
        Assert.Equal(0.4, restored.AreaOpacity);
    }

    /// <summary>
    /// Reported (2026-08-11): "Callout persists, Angle Tool does not." AngleObject's isolated
    /// JsonConverter round-trip already worked (see the other tests in this class); this test
    /// goes through the full ChartDrawingRepository Save/Load path (real file write) instead, to
    /// rule out (or confirm) a repository-level cause specific to AngleObject.
    /// </summary>
    [Fact]
    public async Task AngleObject_RoundTrips_ViaFullRepository_IncludingFileWrite()
    {
        var repo = new ChartDrawingRepository();
        var angle = new AngleObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m))
        {
            Color = Colors.Green,
            Thickness = 1.0
        };
        var data = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject> { angle }
        };

        await repo.SaveAsync("ZZTEST-ANGLE", TimeframeType.Daily, data);
        var restored = repo.Load("ZZTEST-ANGLE", TimeframeType.Daily);

        Assert.NotNull(restored);
        Assert.Single(restored![ChartDrawingContextType.Standard]);
        Assert.Equal(ChartObjectType.AngleTool, restored[ChartDrawingContextType.Standard][0].Type);

        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        var path = Path.Combine(dir, $"ZZTEST-ANGLE.{TimeframeType.Daily}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void RectangleObject_RoundTrips_PointsFillPropertiesAndColor()
    {
        var original = new RectangleObject(
            new ChartPoint(new DateTime(2024, 1, 1), 100m),
            new ChartPoint(new DateTime(2024, 2, 1), 150m))
        {
            Color = Colors.Red,
            Thickness = 2.0,
            IsFilled = true,
            FillAlpha = 50,
            BlendMode = DrawingBlendMode.Multiply
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.Rectangle, restored.Type);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(original.Points[0].Price, restored.Points[0].Price);
        Assert.Equal(original.Points[1].Price, restored.Points[1].Price);
        Assert.Equal(Colors.Red, restored.Color);
        Assert.Equal(2.0, restored.Thickness);
        Assert.True(restored.IsFilled);
        Assert.Equal(50, restored.FillAlpha);
        Assert.Equal(DrawingBlendMode.Multiply, restored.BlendMode);
    }

    [Fact]
    public void FibonacciRetracementObject_RoundTrips_PointsAndColor()
    {
        var original = new FibonacciRetracementObject(
            new ChartPoint(new DateTime(2024, 1, 1), 50m),
            new ChartPoint(new DateTime(2024, 1, 10), 100m))
        {
            Color = Colors.Green,
            Thickness = 1.5
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.FibonacciRetracement, restored.Type);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(50m, restored.Points[0].Price);
        Assert.Equal(100m, restored.Points[1].Price);
        Assert.Equal(Colors.Green, restored.Color);
        Assert.Equal(1.5, restored.Thickness);
    }

    [Fact]
    public void ArrowObject_RoundTrips_PointsAndColor()
    {
        var original = new ArrowObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 5), 20m))
        {
            Color = Colors.Purple,
            Thickness = 3.0
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.Arrow, restored.Type);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(10m, restored.Points[0].Price);
        Assert.Equal(20m, restored.Points[1].Price);
        Assert.Equal(Colors.Purple, restored.Color);
        Assert.Equal(3.0, restored.Thickness);
    }

    [Fact]
    public void RayObject_RoundTrips_PointsAndColor()
    {
        var original = new RayObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 5), 20m))
        {
            Color = Colors.Cyan,
            Thickness = 1.0
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.Ray, restored.Type);
        Assert.Equal(2, restored.Points.Count);
        Assert.Equal(10m, restored.Points[0].Price);
        Assert.Equal(20m, restored.Points[1].Price);
        Assert.Equal(Colors.Cyan, restored.Color);
        Assert.Equal(1.0, restored.Thickness);
    }

    [Fact]
    public void EllipseAnnulusObject_RoundTrips_DespiteBeingRetiredFromTheDrawingToolMenu()
    {
        // Regression guard: EllipseAnnulusObject was removed from DrawingToolCategoryService's Shapes
        // category (its capability now lives in EllipseObject.InnerRadiusRatio), but the class itself
        // must keep deserializing/round-tripping unchanged so previously saved workspaces still load,
        // since ChartObjectTypeRegistry resolves $type by reflecting over every non-abstract
        // IChartObject implementation regardless of whether it is reachable from the tool menu.
        var original = new EllipseAnnulusObject(
            new ChartPoint(new DateTime(2024, 1, 1), 100m),
            new ChartPoint(new DateTime(2024, 1, 11), 150m),
            new ChartPoint(new DateTime(2024, 1, 3), 120m),
            new ChartPoint(new DateTime(2024, 1, 9), 130m),
            new ChartPoint(new DateTime(2024, 1, 6), 125m))
        {
            Color = Colors.Teal,
            IsCircular = true,
            InnerRadiusRatio = 0.4
        };

        var restored = RoundTrip(original);

        Assert.Equal(ChartObjectType.EllipseAnnulus, restored.Type);
        Assert.Equal(5, restored.Points.Count);
        Assert.Equal(Colors.Teal, restored.Color);
        Assert.True(((EllipseAnnulusObject)restored).IsCircular);
        Assert.Equal(0.4, ((EllipseAnnulusObject)restored).InnerRadiusRatio, 2);
    }

    [Fact]
    public void IsLocked_RoundTrips_AcrossMultipleConcreteDrawingObjectTypes()
    {
        var hLine = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 1), 150m))
        {
            IsLocked = true,
            IsVisible = true,
            ZIndex = 2
        };
        var text = new TextObject(new ChartPoint(new DateTime(2024, 1, 1), 200m))
        {
            IsLocked = true,
            IsVisible = false,
            ZIndex = 4
        };
        var triangle = new TriangleObject(
            new ChartPoint(new DateTime(2024, 1, 1), 100m),
            new ChartPoint(new DateTime(2024, 1, 2), 120m),
            new ChartPoint(new DateTime(2024, 1, 3), 110m))
        {
            IsLocked = true,
            ZIndex = 1
        };

        var restoredHLine = RoundTrip(hLine);
        var restoredText = RoundTrip(text);
        var restoredTriangle = RoundTrip(triangle);

        Assert.True(restoredHLine.IsLocked);
        Assert.True(restoredHLine.IsVisible);
        Assert.Equal(2, restoredHLine.ZIndex);

        Assert.True(restoredText.IsLocked);
        Assert.False(restoredText.IsVisible);
        Assert.Equal(4, restoredText.ZIndex);

        Assert.True(restoredTriangle.IsLocked);
        Assert.Equal(1, restoredTriangle.ZIndex);
    }

    [Fact]
    public void CustomName_RoundTrips_AcrossRelativeGeometricRendererAndStandaloneImplementers()
    {
        var trend = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m))
        { CustomName = "My Trend" };
        var hline = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 1), 150m))
        { CustomName = "My HLine" };

        var restoredTrend = RoundTrip(trend);
        var restoredHLine = RoundTrip(hline);

        Assert.Equal("My Trend", restoredTrend.CustomName);
        Assert.Equal("My HLine", restoredHLine.CustomName);
    }

    [Fact]
    public void MoveAxisMode_RoundTrips_AcrossRelativeGeometricRendererAndStandaloneImplementers()
    {
        var trend = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m))
        { MoveAxisMode = DrawingMoveAxisMode.X };
        var hline = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 1), 150m))
        { MoveAxisMode = DrawingMoveAxisMode.Y };

        var restoredTrend = RoundTrip(trend);
        var restoredHLine = RoundTrip(hline);

        Assert.Equal(DrawingMoveAxisMode.X, restoredTrend.MoveAxisMode);
        Assert.Equal(DrawingMoveAxisMode.Y, restoredHLine.MoveAxisMode);
    }

    [Fact]
    public void IsMoveAxisModeExplicit_RoundTrips_AcrossRelativeGeometricRendererAndStandaloneImplementers()
    {
        // Proves the fix for HasExplicitMoveAxisMode's cross-restart limitation: this flag must
        // survive a JSON round-trip (unlike the session-only HashSet it replaced), so an object
        // explicitly set to XY before a restart is still recognized as explicit afterward.
        var trend = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m))
        { IsMoveAxisModeExplicit = true };
        var hline = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 1), 150m))
        { IsMoveAxisModeExplicit = false };

        var restoredTrend = RoundTrip(trend);
        var restoredHLine = RoundTrip(hline);

        Assert.True(restoredTrend.IsMoveAxisModeExplicit);
        Assert.False(restoredHLine.IsMoveAxisModeExplicit);
    }
}
