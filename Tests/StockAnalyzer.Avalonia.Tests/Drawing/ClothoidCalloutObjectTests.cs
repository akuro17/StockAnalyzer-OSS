using System;
using System.Text.Json;
using Avalonia;
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

public class ClothoidCalloutObjectTests
{
    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var obj = new ClothoidCalloutObject();

        Assert.Equal(ChartObjectType.ClothoidCallout, obj.Type);
        Assert.Equal(0.3, obj.CurvatureIntensity);
        Assert.Equal(DrawingStrokeStyle.Dash, obj.LeaderStrokeStyle);
        Assert.Equal("Callout", obj.Text);
        Assert.Equal(DrawingThemeContext.DrawingFontSize, obj.FontSize);
        Assert.Equal(TextHorizontalAlignment.Left, obj.Alignment);
        Assert.True(obj.ShowBackgroundBox);
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

        var obj = new ClothoidCalloutObject(p1, p2);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p1, obj.Points[0]);
        Assert.Equal(p2, obj.Points[1]);
    }

    [Fact]
    public void TypeRegistry_DiscoversClothoidCalloutObject()
    {
        var registeredTypes = ChartObjectTypeRegistry.GetRegisteredTypes();

        Assert.NotNull(registeredTypes);
        Assert.Contains(nameof(ClothoidCalloutObject), registeredTypes.Keys);
        Assert.Equal(typeof(ClothoidCalloutObject), registeredTypes[nameof(ClothoidCalloutObject)]);
        Assert.Equal(typeof(ClothoidCalloutObject), ChartObjectTypeRegistry.Resolve(nameof(ClothoidCalloutObject)));
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_ResolvesClothoidCalloutBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ClothoidCallout);

        Assert.NotNull(behavior);
        Assert.IsType<ClothoidCalloutBehavior>(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
    }

    [Fact]
    public void DrawingToolCategoryService_DoesNotContainClothoidCalloutInTextCategory()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var textCategory = Assert.Single(categories, c => c.NameKey == "DrawCat_Text");

        Assert.DoesNotContain(textCategory.Tools, t => t.Tool == DrawingTool.ClothoidCallout);
    }

    [Fact]
    public void Render_FewerThanTwoPoints_DoesNotThrow()
    {
        var obj = new ClothoidCalloutObject();
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
        var obj = new ClothoidCalloutObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m))
        {
            Text = "Clothoid Annotation\nMulti-line Note"
        };
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(600, 600));

        // 1. Default (Dashed leader, background box on)
        obj.Render(surface.Canvas, transform);

        // 2. Solid leader
        obj.LeaderStrokeStyle = DrawingStrokeStyle.Solid;
        obj.Render(surface.Canvas, transform);

        // 3. Dotted leader
        obj.LeaderStrokeStyle = DrawingStrokeStyle.Dot;
        obj.Render(surface.Canvas, transform);

        // 4. Background box hidden
        obj.ShowBackgroundBox = false;
        obj.Render(surface.Canvas, transform);

        // 5. Anchor inside text box (coincident points)
        var coincidentObj = new ClothoidCalloutObject(new ChartPoint(t2, 150m), new ChartPoint(t2, 150m));
        coincidentObj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_AccurateProximityDetection()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidCalloutObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 100m))
        {
            CurvatureIntensity = 0.0 // Straight leader line
        };
        var transform = CreateTransform();

        var s1 = transform.ChartToScreen(obj.Points[0]);
        var s2 = transform.ChartToScreen(obj.Points[1]);

        // Anchor point
        Assert.True(obj.HitTest(new Point(s1.X, s1.Y), transform, tolerance: 5.0));

        // Text box center
        Assert.True(obj.HitTest(new Point(s2.X, s2.Y), transform, tolerance: 5.0));

        // Midpoint along leader line
        var midScreen = new Point((s1.X + s2.X) * 0.5, (s1.Y + s2.Y) * 0.5);
        Assert.True(obj.HitTest(midScreen, transform, tolerance: 5.0));

        // Far away point
        Assert.False(obj.HitTest(new Point(midScreen.X, midScreen.Y + 150), transform, tolerance: 5.0));
    }

    [Fact]
    public void JsonSerializationRoundtrip_PreservesAllProperties()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var original = new ClothoidCalloutObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 140m))
        {
            CurvatureIntensity = -0.4,
            LeaderStrokeStyle = DrawingStrokeStyle.Dot,
            Text = "Key Level Support",
            FontSize = 18.0,
            Alignment = TextHorizontalAlignment.Center,
            ShowBackgroundBox = true,
            BackgroundPadding = 10f,
            CornerRadius = 8f,
            Thickness = 2.5,
            Color = Colors.CadetBlue
        };

        var options = new JsonSerializerOptions();
        options.Converters.Add(new ChartObjectJsonConverter());
        options.Converters.Add(new AvaloniaColorJsonConverter());

        string json = JsonSerializer.Serialize<IChartObject>(original, options);
        var deserialized = JsonSerializer.Deserialize<IChartObject>(json, options) as ClothoidCalloutObject;

        Assert.NotNull(deserialized);
        Assert.Equal(ChartObjectType.ClothoidCallout, deserialized.Type);
        Assert.Equal(2, deserialized.Points.Count);
        Assert.Equal(original.Points[0], deserialized.Points[0]);
        Assert.Equal(original.Points[1], deserialized.Points[1]);
        Assert.Equal(-0.4, deserialized.CurvatureIntensity);
        Assert.Equal(DrawingStrokeStyle.Dot, deserialized.LeaderStrokeStyle);
        Assert.Equal("Key Level Support", deserialized.Text);
        Assert.Equal(18.0, deserialized.FontSize);
        Assert.Equal(TextHorizontalAlignment.Center, deserialized.Alignment);
        Assert.True(deserialized.ShowBackgroundBox);
        Assert.Equal(10f, deserialized.BackgroundPadding);
        Assert.Equal(8f, deserialized.CornerRadius);
        Assert.Equal(2.5, deserialized.Thickness);
        Assert.Equal(Colors.CadetBlue, deserialized.Color);
    }
}
