using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using Xunit;
using AvaloniaPoint = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class TriangleObjectEnhancementTests
{
    private static readonly DateTime StartTime = new(2026, 1, 1);

    [Fact]
    public void DisplayOptions_DefaultOff_AndAppearInDynamicSettingsMetadata()
    {
        var triangle = CreateTriangle();

        Assert.False(triangle.ShowInteriorAngles);
        Assert.False(triangle.ShowReflectedTriangle);
        Assert.False(triangle.ShowCentroid);
        Assert.False(triangle.ShowCircumcenter);
        Assert.False(triangle.ShowIncenter);
        Assert.False(triangle.ShowOrthocenter);
        Assert.False(triangle.ShowExcenters);

        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(TriangleObject));
        var angleSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowInteriorAngles)));
        var reflectionSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowReflectedTriangle)));
        var centroidSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowCentroid)));
        var circumcenterSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowCircumcenter)));
        var incenterSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowIncenter)));
        var orthocenterSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowOrthocenter)));
        var excentersSetting = Assert.Single(metadata.Where(item => item.Prop.Name == nameof(TriangleObject.ShowExcenters)));

        Assert.Contains(DrawingParameterTags.Analysis, angleSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, reflectionSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, centroidSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, circumcenterSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, incenterSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, orthocenterSetting.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, excentersSetting.Tags);
    }

    [Fact]
    public void FiveCenterGeometry_UsesExactThreeFourFiveTriangleValues()
    {
        bool success = TriangleObject.TryGetCenterGeometry(
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(4, 0),
            new AvaloniaPoint(0, 3),
            out var geometry);

        Assert.True(success);
        AssertPoint(geometry.Centroid, 4.0 / 3.0, 1.0);
        AssertPoint(geometry.Circumcenter, 2.0, 1.5);
        Assert.Equal(2.5, geometry.Circumradius, 8);
        AssertPoint(geometry.Incenter, 1.0, 1.0);
        Assert.Equal(1.0, geometry.Inradius, 8);
        AssertPoint(geometry.Orthocenter, 0.0, 0.0);
        AssertPoint(geometry.ExcenterA, 6.0, 6.0);
        Assert.Equal(6.0, geometry.ExradiusA, 8);
        AssertPoint(geometry.ExcenterB, -2.0, 2.0);
        Assert.Equal(2.0, geometry.ExradiusB, 8);
        AssertPoint(geometry.ExcenterC, 3.0, -3.0);
        Assert.Equal(3.0, geometry.ExradiusC, 8);

        Assert.Equal(0.0, Cross(geometry.ExcenterB, new AvaloniaPoint(0, 0), geometry.ExcenterC), 8);
        Assert.Equal(0.0, Cross(geometry.ExcenterA, new AvaloniaPoint(4, 0), geometry.ExcenterC), 8);
        Assert.Equal(0.0, Cross(geometry.ExcenterA, new AvaloniaPoint(0, 3), geometry.ExcenterB), 8);
    }

    [Fact]
    public void FiveCenterGeometry_RejectsDegenerateTriangle()
    {
        Assert.False(TriangleObject.TryGetCenterGeometry(
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(1, 1),
            new AvaloniaPoint(2, 2),
            out _));
    }

    [Theory]
    [InlineData(0, 0, 4, 0, 2, 3)]
    [InlineData(0, 0, 4, 0, 1, 1)]
    public void FiveCenterGeometry_PreservesAcuteAndObtuseGeometryInvariants(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        var a = new AvaloniaPoint(ax, ay);
        var b = new AvaloniaPoint(bx, by);
        var c = new AvaloniaPoint(cx, cy);
        Assert.True(TriangleObject.TryGetCenterGeometry(a, b, c, out var geometry));

        Assert.Equal(geometry.Circumradius, Distance(geometry.Circumcenter, a), 8);
        Assert.Equal(geometry.Circumradius, Distance(geometry.Circumcenter, b), 8);
        Assert.Equal(geometry.Circumradius, Distance(geometry.Circumcenter, c), 8);

        Assert.Equal(geometry.Inradius, DistanceToLine(geometry.Incenter, a, b), 8);
        Assert.Equal(geometry.Inradius, DistanceToLine(geometry.Incenter, b, c), 8);
        Assert.Equal(geometry.Inradius, DistanceToLine(geometry.Incenter, c, a), 8);

        Assert.Equal(0.0, Dot(geometry.Orthocenter, a, b, c), 8);
        Assert.Equal(0.0, Dot(geometry.Orthocenter, b, c, a), 8);
        Assert.Equal(0.0, Dot(geometry.Orthocenter, c, a, b), 8);

        AssertExcenterDistances(geometry.ExcenterA, geometry.ExradiusA, a, b, c);
        AssertExcenterDistances(geometry.ExcenterB, geometry.ExradiusB, a, b, c);
        AssertExcenterDistances(geometry.ExcenterC, geometry.ExradiusC, a, b, c);
    }

    [Fact]
    public void InteriorAngles_AreCalculatedFromDisplayedTriangleGeometry()
    {
        var rightAngle = TriangleObject.GetInteriorAngleDegrees(
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(10, 0),
            new AvaloniaPoint(0, 10));
        var firstAcuteAngle = TriangleObject.GetInteriorAngleDegrees(
            new AvaloniaPoint(10, 0),
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(0, 10));
        var secondAcuteAngle = TriangleObject.GetInteriorAngleDegrees(
            new AvaloniaPoint(0, 10),
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(10, 0));

        Assert.Equal(90.0, rightAngle, 8);
        Assert.Equal(45.0, firstAcuteAngle, 8);
        Assert.Equal(45.0, secondAcuteAngle, 8);
        Assert.Equal(180.0, rightAngle + firstAcuteAngle + secondAcuteAngle, 8);
    }

    [Theory]
    [InlineData(0, 0, 20)]
    [InlineData(1, 10, 0)]
    [InlineData(2, -10, 10)]
    public void ReflectedVertex_UsesSideOppositeCurrentAnchorPoint(int anchorPointIndex, double expectedX, double expectedY)
    {
        bool success = TriangleObject.TryGetReflectedVertex(
            new AvaloniaPoint(0, 0),
            new AvaloniaPoint(0, 10),
            new AvaloniaPoint(10, 10),
            anchorPointIndex,
            out var reflected,
            out _,
            out _);

        Assert.True(success);
        Assert.Equal(expectedX, reflected.X, 8);
        Assert.Equal(expectedY, reflected.Y, 8);
    }

    [Fact]
    public void Render_OptionsAddTheirRequestedVisuals()
    {
        var transform = CreateTransform();
        var triangle = CreateTriangle();

        using var baseline = Render(triangle, transform);

        triangle.ShowInteriorAngles = true;
        using var withAngles = Render(triangle, transform);
        Assert.True(CountVisiblePixels(withAngles) > CountVisiblePixels(baseline));

        triangle.ShowInteriorAngles = false;
        triangle.ShowReflectedTriangle = true;
        using var withReflection = Render(triangle, transform);

        Assert.Equal((byte)0, baseline.GetPixel(20, 75).Alpha);
        Assert.True(withReflection.GetPixel(20, 75).Alpha > 0);
    }

    [Theory]
    [InlineData(nameof(TriangleObject.ShowCentroid))]
    [InlineData(nameof(TriangleObject.ShowCircumcenter))]
    [InlineData(nameof(TriangleObject.ShowIncenter))]
    [InlineData(nameof(TriangleObject.ShowOrthocenter))]
    [InlineData(nameof(TriangleObject.ShowExcenters))]
    public void Render_FiveCenterOptionDrawsMarkerAtItsCalculatedCenter(string propertyName)
    {
        var triangle = CreateAcuteTriangle();
        var transform = CreateTransform();
        Assert.True(TriangleObject.TryGetCenterGeometry(
            transform.ChartToScreen(triangle.Points[0]),
            transform.ChartToScreen(triangle.Points[1]),
            transform.ChartToScreen(triangle.Points[2]),
            out var geometry));

        var property = typeof(TriangleObject).GetProperty(propertyName);
        Assert.NotNull(property);
        property.SetValue(triangle, true);
        using var withCenter = Render(triangle, transform, 200);

        var center = propertyName switch
        {
            nameof(TriangleObject.ShowCentroid) => geometry.Centroid,
            nameof(TriangleObject.ShowCircumcenter) => geometry.Circumcenter,
            nameof(TriangleObject.ShowIncenter) => geometry.Incenter,
            nameof(TriangleObject.ShowOrthocenter) => geometry.Orthocenter,
            nameof(TriangleObject.ShowExcenters) => geometry.ExcenterA,
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName))
        };

        Assert.Equal(triangle.SkiaColor, withCenter.GetPixel((int)Math.Round(center.X), (int)Math.Round(center.Y)));
    }

    [Fact]
    public void OrthocenterCircles_UseEachVertexToOrthocenterAsDiameter()
    {
        var triangle = CreateTriangle();
        triangle.ShowOrthocenter = true;

        using var rendered = Render(triangle, CreateTransform());

        Assert.True(rendered.GetPixel(40, 40).Alpha > 0);
        Assert.True(rendered.GetPixel(50, 30).Alpha > 0);
    }

    [Fact]
    public void OrthocenterMarker_InheritsTriangleColor()
    {
        var triangle = CreateAcuteTriangle();
        var transform = CreateTransform();
        triangle.ShowOrthocenter = true;
        Assert.True(TriangleObject.TryGetCenterGeometry(
            transform.ChartToScreen(triangle.Points[0]),
            transform.ChartToScreen(triangle.Points[1]),
            transform.ChartToScreen(triangle.Points[2]),
            out var geometry));

        using var rendered = Render(triangle, transform);
        int orthocenterX = (int)Math.Round(geometry.Orthocenter.X);
        int orthocenterY = (int)Math.Round(geometry.Orthocenter.Y);

        Assert.Equal(triangle.SkiaColor, rendered.GetPixel(orthocenterX, orthocenterY));
    }

    [Fact]
    public void InteriorAngleText_UsesThemeMainTextColorAndFontsDetailSize()
    {
        var textColorProperty = typeof(DrawingThemeContext).GetProperty(nameof(DrawingThemeContext.MainTextSkColor));
        var detailFontSizeProperty = typeof(DrawingThemeContext).GetProperty(nameof(DrawingThemeContext.FontSize));
        var drawingFontSizeProperty = typeof(DrawingThemeContext).GetProperty(nameof(DrawingThemeContext.DrawingFontSize));
        Assert.NotNull(textColorProperty);
        Assert.NotNull(detailFontSizeProperty);
        Assert.NotNull(drawingFontSizeProperty);

        var originalColor = DrawingThemeContext.MainTextSkColor;
        float originalDetailSize = DrawingThemeContext.DetailFontSize;
        float originalDrawingSize = DrawingThemeContext.DrawingFontSize;
        var expectedColor = new SKColor(12, 34, 56);
        const float expectedDetailSize = 19f;
        const float unrelatedDrawingSize = 27f;

        try
        {
            textColorProperty.SetValue(null, expectedColor);
            detailFontSizeProperty.SetValue(null, expectedDetailSize);
            drawingFontSizeProperty.SetValue(null, unrelatedDrawingSize);

            var triangle = CreateTriangle();
            triangle.ShowInteriorAngles = true;
            using var bitmap = Render(triangle, CreateTransform());
            int detailFontPixelCount = CountPixelsOfColor(bitmap, expectedColor);
            Assert.True(detailFontPixelCount > 0);

            drawingFontSizeProperty.SetValue(null, expectedDetailSize);
            using var drawingFontChanged = Render(triangle, CreateTransform());
            Assert.Equal(detailFontPixelCount, CountPixelsOfColor(drawingFontChanged, expectedColor));

            detailFontSizeProperty.SetValue(null, unrelatedDrawingSize);
            using var detailFontChanged = Render(triangle, CreateTransform());
            Assert.True(CountPixelsOfColor(detailFontChanged, expectedColor) > detailFontPixelCount);
        }
        finally
        {
            textColorProperty.SetValue(null, originalColor);
            detailFontSizeProperty.SetValue(null, originalDetailSize);
            drawingFontSizeProperty.SetValue(null, originalDrawingSize);
        }
    }

    [Fact]
    public void AdvanceAnchorPoint_RaisesRedrawNotification_AndChangesReflectedGeometry()
    {
        var transform = CreateTransform();
        var triangle = CreateTriangle();
        var manager = new ChartObjectManager();
        int changedCount = 0;
        manager.Changed += () => changedCount++;
        manager.AddObject(triangle);
        changedCount = 0;

        var p1 = transform.ChartToScreen(triangle.Points[0]);
        var p2 = transform.ChartToScreen(triangle.Points[1]);
        var p3 = transform.ChartToScreen(triangle.Points[2]);
        Assert.True(TriangleObject.TryGetReflectedVertex(p1, p2, p3, triangle.AnchorPointIndex, out var before, out _, out _));

        Assert.True(manager.AdvanceAnchorPoint(triangle.Id));
        Assert.True(TriangleObject.TryGetReflectedVertex(p1, p2, p3, triangle.AnchorPointIndex, out var after, out _, out _));

        Assert.Equal(1, changedCount);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DisplayOptions_RoundTripThroughDrawingPersistence()
    {
        var triangle = CreateTriangle();
        triangle.ShowInteriorAngles = true;
        triangle.ShowReflectedTriangle = true;
        triangle.ShowCentroid = true;
        triangle.ShowCircumcenter = true;
        triangle.ShowIncenter = true;
        triangle.ShowOrthocenter = true;
        triangle.ShowExcenters = true;

        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new ChartObjectJsonConverter(),
                new AvaloniaColorJsonConverter(),
                new JsonStringEnumConverter()
            }
        };

        string json = JsonSerializer.Serialize<IChartObject>(triangle, options);
        var restored = Assert.IsType<TriangleObject>(JsonSerializer.Deserialize<IChartObject>(json, options));

        Assert.True(restored.ShowInteriorAngles);
        Assert.True(restored.ShowReflectedTriangle);
        Assert.True(restored.ShowCentroid);
        Assert.True(restored.ShowCircumcenter);
        Assert.True(restored.ShowIncenter);
        Assert.True(restored.ShowOrthocenter);
        Assert.True(restored.ShowExcenters);
    }

    [Theory]
    [InlineData(TriangleRetirementPath.Remove)]
    [InlineData(TriangleRetirementPath.DeleteAll)]
    [InlineData(TriangleRetirementPath.Clear)]
    [InlineData(TriangleRetirementPath.LoadSnapshot)]
    public void RetiringTriangle_DisposesCachedSkiaResources(TriangleRetirementPath retirementPath)
    {
        var manager = new ChartObjectManager();
        var triangle = CreateTriangle();
        manager.AddObject(triangle);

        switch (retirementPath)
        {
            case TriangleRetirementPath.Remove:
                Assert.True(manager.RemoveObject(triangle.Id));
                break;
            case TriangleRetirementPath.DeleteAll:
                manager.DeleteAll();
                break;
            case TriangleRetirementPath.Clear:
                manager.Clear();
                break;
            case TriangleRetirementPath.LoadSnapshot:
                manager.LoadSnapshot(new Dictionary<ChartDrawingContextType, List<IChartObject>>
                {
                    [manager.CurrentContext] = new List<IChartObject>()
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(retirementPath));
        }

        Assert.Throws<ObjectDisposedException>(() => Render(triangle, CreateTransform()));
    }

    public enum TriangleRetirementPath
    {
        Remove,
        DeleteAll,
        Clear,
        LoadSnapshot
    }

    private static TriangleObject CreateTriangle()
        => new(
            new ChartPoint(StartTime.AddDays(2), 8m),
            new ChartPoint(StartTime.AddDays(2), 4m),
            new ChartPoint(StartTime.AddDays(8), 4m));

    private static TriangleObject CreateAcuteTriangle()
        => new(
            new ChartPoint(StartTime.AddDays(5), 8m),
            new ChartPoint(StartTime.AddDays(1), 2m),
            new ChartPoint(StartTime.AddDays(9), 2m));

    private static LinearCoordinateTransform CreateTransform()
        => new(StartTime, StartTime.AddDays(10), 0m, 10m, 100, 100);

    private static SKBitmap Render(TriangleObject triangle, ICoordinateTransform transform, int bitmapSize = 100)
    {
        var bitmap = new SKBitmap(bitmapSize, bitmapSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        triangle.Render(canvas, transform);
        canvas.Flush();
        return bitmap;
    }

    private static int CountVisiblePixels(SKBitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0) count++;
            }
        }
        return count;
    }

    private static int CountPixelsOfColor(SKBitmap bitmap, SKColor color)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color) count++;
            }
        }

        return count;
    }

    private static void AssertPoint(AvaloniaPoint actual, double expectedX, double expectedY)
    {
        Assert.Equal(expectedX, actual.X, 8);
        Assert.Equal(expectedY, actual.Y, 8);
    }

    private static double Cross(AvaloniaPoint first, AvaloniaPoint middle, AvaloniaPoint last)
        => (middle.X - first.X) * (last.Y - first.Y) -
           (middle.Y - first.Y) * (last.X - first.X);

    private static double Distance(AvaloniaPoint first, AvaloniaPoint second)
    {
        double x = second.X - first.X;
        double y = second.Y - first.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static double DistanceToLine(AvaloniaPoint point, AvaloniaPoint lineStart, AvaloniaPoint lineEnd)
        => Math.Abs(Cross(lineStart, lineEnd, point)) / Distance(lineStart, lineEnd);

    private static double Dot(
        AvaloniaPoint lineStart,
        AvaloniaPoint lineEnd,
        AvaloniaPoint perpendicularStart,
        AvaloniaPoint perpendicularEnd)
        => (lineEnd.X - lineStart.X) * (perpendicularEnd.X - perpendicularStart.X) +
           (lineEnd.Y - lineStart.Y) * (perpendicularEnd.Y - perpendicularStart.Y);

    private static void AssertExcenterDistances(
        AvaloniaPoint excenter,
        double exradius,
        AvaloniaPoint a,
        AvaloniaPoint b,
        AvaloniaPoint c)
    {
        Assert.Equal(exradius, DistanceToLine(excenter, a, b), 8);
        Assert.Equal(exradius, DistanceToLine(excenter, b, c), 8);
        Assert.Equal(exradius, DistanceToLine(excenter, c, a), 8);
    }
}
