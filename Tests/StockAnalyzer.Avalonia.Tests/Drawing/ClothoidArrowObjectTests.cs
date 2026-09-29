using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Point = Avalonia.Point;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class ClothoidArrowObjectTests
{
    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var obj = new ClothoidArrowObject();

        Assert.Equal(ChartObjectType.ClothoidArrow, obj.Type);
        Assert.Equal(0.3, obj.CurvatureIntensity);
        Assert.Equal(15f, obj.ArrowHeadSize);
        Assert.Equal(25f, obj.ArrowHeadAngle);
        Assert.True(obj.IsHeadFilled);
        Assert.Equal(DrawingStrokeStyle.Solid, obj.StrokeStyle);
        Assert.Equal(DrawingStrokeStyle.Solid, obj.Style);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
        Assert.True(obj.IsVisible);
        Assert.False(obj.IsLocked);
        Assert.Empty(obj.Points);
    }

    [Fact]
    public void TwoPointConstructor_SetsPoints()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var p1 = new ChartPoint(t1, 100m);
        var p2 = new ChartPoint(t2, 120m);

        var obj = new ClothoidArrowObject(p1, p2);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p1, obj.Points[0]);
        Assert.Equal(p2, obj.Points[1]);
    }

    [Fact]
    public void TypeRegistry_DiscoversClothoidArrowObject()
    {
        var registeredTypes = ChartObjectTypeRegistry.GetRegisteredTypes();

        Assert.NotNull(registeredTypes);
        Assert.Contains(nameof(ClothoidArrowObject), registeredTypes.Keys);
        Assert.Equal(typeof(ClothoidArrowObject), registeredTypes[nameof(ClothoidArrowObject)]);
        Assert.Equal(typeof(ClothoidArrowObject), ChartObjectTypeRegistry.Resolve(nameof(ClothoidArrowObject)));
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_ResolvesClothoidArrowBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ClothoidArrow);

        Assert.NotNull(behavior);
        Assert.IsType<ClothoidArrowBehavior>(behavior);
        Assert.Equal(0, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void DrawingToolCategoryService_ContainsClothoidArrowInLinesCategory()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var linesCategory = Assert.Single(categories, c => c.NameKey == "DrawCat_Lines");

        Assert.Contains(linesCategory.Tools, t => t.Tool == DrawingTool.ClothoidArrow);
    }

    [Fact]
    public void Render_FewerThanTwoPoints_DoesNotThrow()
    {
        var obj = new ClothoidArrowObject();
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));

        // 0 points
        obj.Render(surface.Canvas, transform);

        // 1 point
        obj.Points.Add(new ChartPoint(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m));
        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void Render_TwoPoints_DrawsSuccessfullyAcrossStyles()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidArrowObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m));
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(500, 500));

        // 1. Default (Solid, filled head)
        obj.Render(surface.Canvas, transform);

        // 2. Open head
        obj.IsHeadFilled = false;
        obj.Render(surface.Canvas, transform);

        // 3. Dashed stroke
        obj.StrokeStyle = DrawingStrokeStyle.Dash;
        obj.Render(surface.Canvas, transform);

        // 4. Dotted stroke
        obj.StrokeStyle = DrawingStrokeStyle.Dot;
        obj.Render(surface.Canvas, transform);

        // 5. Inverted curvature
        obj.CurvatureIntensity = -0.5;
        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_AccurateProximityDetection()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidArrowObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m))
        {
            CurvatureIntensity = 0.0 // Straight line for direct coordinate predictability
        };
        var transform = CreateTransform();

        var s1 = transform.ChartToScreen(obj.Points[0]);
        var s2 = transform.ChartToScreen(obj.Points[1]);

        // Midpoint of segment
        var midScreen = new Point((s1.X + s2.X) * 0.5, (s1.Y + s2.Y) * 0.5);
        Assert.True(obj.HitTest(midScreen, transform, tolerance: 5.0));

        // Start point
        Assert.True(obj.HitTest(new Point(s1.X, s1.Y), transform, tolerance: 5.0));

        // End point (head)
        Assert.True(obj.HitTest(new Point(s2.X, s2.Y), transform, tolerance: 5.0));

        // Far away point
        Assert.False(obj.HitTest(new Point(midScreen.X, midScreen.Y + 100), transform, tolerance: 5.0));

        // Empty points returns false
        var emptyObj = new ClothoidArrowObject();
        Assert.False(emptyObj.HitTest(midScreen, transform, tolerance: 5.0));
    }

    [Fact]
    public void GetCalculatedValues_ReturnsMetrics()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidArrowObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 120m))
        {
            CurvatureIntensity = 0.45
        };

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "ArrowPrice");
        Assert.Contains(values, v => v.Key == "Direction");
        Assert.Contains(values, v => v.Key == "PriceDelta" && v.NumericValue == 20m);
        Assert.Contains(values, v => v.Key == "Duration" && v.NumericValue == 10m);
        Assert.Contains(values, v => v.Key == "Curvature" && v.NumericValue == 0.45m);
    }

    [Fact]
    public void JsonSerializationRoundtrip_PreservesAllProperties()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var original = new ClothoidArrowObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m))
        {
            CurvatureIntensity = -0.75,
            ArrowHeadSize = 22f,
            ArrowHeadAngle = 35f,
            IsHeadFilled = false,
            StrokeStyle = DrawingStrokeStyle.Dash,
            Thickness = 3.5,
            Color = Colors.Goldenrod
        };

        var options = new JsonSerializerOptions();
        options.Converters.Add(new ChartObjectJsonConverter());
        options.Converters.Add(new AvaloniaColorJsonConverter());

        string json = JsonSerializer.Serialize<IChartObject>(original, options);
        var deserialized = JsonSerializer.Deserialize<IChartObject>(json, options) as ClothoidArrowObject;

        Assert.NotNull(deserialized);
        Assert.Equal(ChartObjectType.ClothoidArrow, deserialized.Type);
        Assert.Equal(2, deserialized.Points.Count);
        Assert.Equal(original.Points[0], deserialized.Points[0]);
        Assert.Equal(original.Points[1], deserialized.Points[1]);
        Assert.Equal(-0.75, deserialized.CurvatureIntensity);
        Assert.Equal(22f, deserialized.ArrowHeadSize);
        Assert.Equal(35f, deserialized.ArrowHeadAngle);
        Assert.False(deserialized.IsHeadFilled);
        Assert.Equal(DrawingStrokeStyle.Dash, deserialized.StrokeStyle);
        Assert.Equal(3.5, deserialized.Thickness);
        Assert.Equal(Colors.Goldenrod, deserialized.Color);
    }

    [Fact]
    public void SettingsPanelDefinition_CanHandleAndDynamicTags_ConfiguredCorrectly()
    {
        var panelDef = new StockAnalyzer.Avalonia.Views.Dialogs.ClothoidArrowSettingsPanelDefinition();
        var arrow = new ClothoidArrowObject();

        Assert.True(panelDef.CanHandle(arrow));
        Assert.False(panelDef.CanHandle(new ClothoidZoneObject()));
        Assert.True(panelDef.UsesDynamicSettings);
        Assert.NotNull(panelDef.DynamicHiddenParameterTags);
        Assert.Contains(StockAnalyzer.Core.Constants.DrawingParameterTags.Common, panelDef.DynamicHiddenParameterTags);
        Assert.Contains(StockAnalyzer.Core.Constants.DrawingParameterTags.Geometry, panelDef.DynamicHiddenParameterTags);
        Assert.DoesNotContain(StockAnalyzer.Core.Constants.DrawingParameterTags.Analysis, panelDef.DynamicHiddenParameterTags);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_GeneratesAllArrowParameters()
    {
        var builder = new StockAnalyzer.Avalonia.Services.DrawingParameterViewBuilder();
        var arrow = new ClothoidArrowObject();
        var panelDef = new StockAnalyzer.Avalonia.Views.Dialogs.ClothoidArrowSettingsPanelDefinition();

        var control = builder.Build(arrow, panelDef.DynamicHiddenParameterTags!);
        Assert.NotNull(control);

        var stackPanel = Assert.IsType<StackPanel>(control);
        var labels = stackPanel.Children
            .OfType<Border>()
            .Select(b => b.Child)
            .OfType<StackPanel>()
            .SelectMany(p => p.Children)
            .Select(c => c switch
            {
                CheckBox cb => cb.Content?.ToString(),
                Grid g => g.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
                _ => null
            })
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        Assert.Contains("Curvature Intensity", labels);
        Assert.Contains("Arrow Head Size", labels);
        Assert.Contains("Arrow Head Angle", labels);
        Assert.Contains("Fill Head", labels);
        Assert.Contains("Stroke Style", labels);
        Assert.DoesNotContain("Move Axis Mode", labels);
        Assert.DoesNotContain("Panel Index", labels);
    }

    [Fact]
    public void MultiPoint_ClothoidArrow_AddsPointsAndRendersPiecewiseClothoid()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var t4 = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var arrow = new ClothoidArrowObject();
        arrow.AddPoint(new ChartPoint(t1, 100m));
        arrow.AddPoint(new ChartPoint(t2, 120m));
        arrow.AddPoint(new ChartPoint(t3, 110m));
        arrow.AddPoint(new ChartPoint(t4, 140m));

        Assert.Equal(4, arrow.Points.Count);

        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(500, 500));
        arrow.Render(surface.Canvas, transform);

        // Hit testing on intermediate point segment
        var screenP2 = transform.ChartToScreen(arrow.Points[1]);
        Assert.True(arrow.HitTest(new Point(screenP2.X, screenP2.Y), transform));

        // Proximity to end arrow head
        var screenEnd = transform.ChartToScreen(arrow.Points[3]);
        Assert.True(arrow.HitTest(new Point(screenEnd.X, screenEnd.Y), transform));

        // Calculated values evaluate start to end
        var values = arrow.GetCalculatedValues(t4);
        var priceDelta = Assert.Single(values, v => v.Key == "PriceDelta");
        Assert.Equal(40m, priceDelta.NumericValue); // 140 - 100
    }
}
