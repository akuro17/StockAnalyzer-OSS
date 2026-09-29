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

public class ClothoidZoneObjectTests
{
    private static ICoordinateTransform CreateTransform() =>
        new GenericCoordinateTransform(ChartAxisMode.Time, 1000, 500);

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var obj = new ClothoidZoneObject();

        Assert.Equal(ChartObjectType.ClothoidZone, obj.Type);
        Assert.Equal(0.5, obj.ZoneStrength);
        Assert.True(obj.IsClosedZone);
        Assert.True(obj.IsFilled);
        Assert.Equal(DrawingBlendMode.Normal, obj.BlendMode);
        Assert.Equal(DrawingGradientType.None, obj.GradientType);
        Assert.Null(obj.GradientEndColor);
        Assert.Equal((byte)30, obj.FillAlpha);
        Assert.Equal((byte)30, obj.GradientEndAlpha);
        Assert.Equal(DrawingStrokeStyle.Solid, obj.StrokeStyle);
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

        var obj = new ClothoidZoneObject(p1, p2);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p1, obj.Points[0]);
        Assert.Equal(p2, obj.Points[1]);
    }

    [Fact]
    public void TypeRegistry_DiscoversClothoidZoneObject()
    {
        var registeredTypes = ChartObjectTypeRegistry.GetRegisteredTypes();

        Assert.NotNull(registeredTypes);
        Assert.Contains(nameof(ClothoidZoneObject), registeredTypes.Keys);
        Assert.Equal(typeof(ClothoidZoneObject), registeredTypes[nameof(ClothoidZoneObject)]);
        Assert.Equal(typeof(ClothoidZoneObject), ChartObjectTypeRegistry.Resolve(nameof(ClothoidZoneObject)));
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_ResolvesClothoidZoneBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.ClothoidZone);

        Assert.NotNull(behavior);
        Assert.IsType<ClothoidZoneBehavior>(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
    }

    [Fact]
    public void DrawingToolCategoryService_DoesNotContainClothoidZoneInShapesCategory()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var shapesCategory = Assert.Single(categories, c => c.NameKey == "DrawCat_Shapes");

        Assert.DoesNotContain(shapesCategory.Tools, t => t.Tool == DrawingTool.ClothoidZone);
    }

    [Fact]
    public void Render_FewerThanTwoPoints_DoesNotThrow()
    {
        var obj = new ClothoidZoneObject();
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
        var obj = new ClothoidZoneObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m));
        var transform = CreateTransform();
        using var surface = SKSurface.Create(new SKImageInfo(600, 600));

        // 1. Default (Closed, Solid fill)
        obj.Render(surface.Canvas, transform);

        // 2. Open zone mode (Upper and Lower boundary curves)
        obj.IsClosedZone = false;
        obj.Render(surface.Canvas, transform);

        // 3. Dashed boundary stroke
        obj.StrokeStyle = DrawingStrokeStyle.Dash;
        obj.Render(surface.Canvas, transform);

        // 4. Dotted boundary stroke
        obj.StrokeStyle = DrawingStrokeStyle.Dot;
        obj.Render(surface.Canvas, transform);

        // 5. Strong zone (Max fillet)
        obj.IsClosedZone = true;
        obj.ZoneStrength = 1.0;
        obj.Render(surface.Canvas, transform);

        // 6. Weak zone (Small fillet)
        obj.ZoneStrength = 0.05;
        obj.Render(surface.Canvas, transform);
    }

    [Fact]
    public void HitTest_AccurateProximityDetection()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidZoneObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 150m))
        {
            IsClosedZone = true,
            IsFilled = true
        };
        var transform = CreateTransform();

        var s1 = transform.ChartToScreen(obj.Points[0]);
        var s2 = transform.ChartToScreen(obj.Points[1]);

        // Center of zone box (filled -> must hit)
        var center = new Point((s1.X + s2.X) * 0.5, (s1.Y + s2.Y) * 0.5);
        Assert.True(obj.HitTest(center, transform, tolerance: 5.0));

        // Corner point
        Assert.True(obj.HitTest(new Point(s1.X, s1.Y), transform, tolerance: 5.0));

        // Far outside zone box
        Assert.False(obj.HitTest(new Point(center.X, center.Y + 300), transform, tolerance: 5.0));
    }

    [Fact]
    public void GetCalculatedValues_ReturnsMetrics()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc);
        var obj = new ClothoidZoneObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 135m))
        {
            ZoneStrength = 0.75
        };

        var values = obj.GetCalculatedValues(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEmpty(values);
        Assert.Contains(values, v => v.Key == "TopPrice" && v.NumericValue == 135m);
        Assert.Contains(values, v => v.Key == "BottomPrice" && v.NumericValue == 100m);
        Assert.Contains(values, v => v.Key == "PriceSpan" && v.NumericValue == 35m);
        Assert.Contains(values, v => v.Key == "Duration" && v.NumericValue == 10m);
        Assert.Contains(values, v => v.Key == "ZoneStrength" && v.NumericValue == 0.75m);
    }

    [Fact]
    public void JsonSerializationRoundtrip_PreservesAllProperties()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var original = new ClothoidZoneObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 160m))
        {
            ZoneStrength = 0.85,
            IsClosedZone = true,
            IsFilled = true,
            BlendMode = DrawingBlendMode.Multiply,
            GradientType = DrawingGradientType.LinearVertical,
            GradientEndColor = Colors.DarkBlue,
            FillAlpha = 55,
            GradientEndAlpha = 90,
            StrokeStyle = DrawingStrokeStyle.Dash,
            Thickness = 2.0,
            Color = Colors.Teal
        };

        var options = new JsonSerializerOptions();
        options.Converters.Add(new ChartObjectJsonConverter());
        options.Converters.Add(new AvaloniaColorJsonConverter());

        string json = JsonSerializer.Serialize<IChartObject>(original, options);
        var deserialized = JsonSerializer.Deserialize<IChartObject>(json, options) as ClothoidZoneObject;

        Assert.NotNull(deserialized);
        Assert.Equal(ChartObjectType.ClothoidZone, deserialized.Type);
        Assert.Equal(2, deserialized.Points.Count);
        Assert.Equal(original.Points[0], deserialized.Points[0]);
        Assert.Equal(original.Points[1], deserialized.Points[1]);
        Assert.Equal(0.85, deserialized.ZoneStrength);
        Assert.True(deserialized.IsClosedZone);
        Assert.True(deserialized.IsFilled);
        Assert.Equal(DrawingBlendMode.Multiply, deserialized.BlendMode);
        Assert.Equal(DrawingGradientType.LinearVertical, deserialized.GradientType);
        Assert.Equal(Colors.DarkBlue, deserialized.GradientEndColor);
        Assert.Equal((byte)55, deserialized.FillAlpha);
        Assert.Equal((byte)90, deserialized.GradientEndAlpha);
        Assert.Equal(DrawingStrokeStyle.Dash, deserialized.StrokeStyle);
        Assert.Equal(2.0, deserialized.Thickness);
        Assert.Equal(Colors.Teal, deserialized.Color);
    }

    [Fact]
    public void SettingsPanelDefinition_CanHandleAndDynamicTags_ConfiguredCorrectly()
    {
        var panelDef = new StockAnalyzer.Avalonia.Views.Dialogs.ClothoidZoneSettingsPanelDefinition();
        var zone = new ClothoidZoneObject();

        Assert.True(panelDef.CanHandle(zone));
        Assert.False(panelDef.CanHandle(new ClothoidArrowObject()));
        Assert.True(panelDef.UsesDynamicSettings);
        Assert.NotNull(panelDef.DynamicHiddenParameterTags);
        Assert.Contains(StockAnalyzer.Core.Constants.DrawingParameterTags.Common, panelDef.DynamicHiddenParameterTags);
        Assert.Contains(StockAnalyzer.Core.Constants.DrawingParameterTags.Geometry, panelDef.DynamicHiddenParameterTags);
        Assert.DoesNotContain(StockAnalyzer.Core.Constants.DrawingParameterTags.Analysis, panelDef.DynamicHiddenParameterTags);
        Assert.DoesNotContain(StockAnalyzer.Core.Constants.DrawingParameterTags.Fill, panelDef.DynamicHiddenParameterTags);
    }

    [AvaloniaFact]
    public void DrawingParameterViewBuilder_GeneratesAllZoneParametersIncludingByteFillAlpha()
    {
        var builder = new StockAnalyzer.Avalonia.Services.DrawingParameterViewBuilder();
        var zone = new ClothoidZoneObject();
        var panelDef = new StockAnalyzer.Avalonia.Views.Dialogs.ClothoidZoneSettingsPanelDefinition();

        var control = builder.Build(zone, panelDef.DynamicHiddenParameterTags!);
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

        Assert.Contains("Zone Strength", labels);
        Assert.Contains("Is Closed Zone", labels);
        Assert.Contains("Stroke Style", labels);
        Assert.True(labels.Contains("Is Filled") || labels.Contains(LocalizationManager.Instance.Get("Param_IsFilled")));
        Assert.Contains("Blend Mode", labels);
        Assert.Contains("Gradient Type", labels);
        Assert.Contains("Gradient End Color", labels);
        Assert.Contains("Fill Alpha", labels);
        Assert.Contains("Gradient End Alpha", labels);
        Assert.DoesNotContain("Move Axis Mode", labels);
        Assert.DoesNotContain("Panel Index", labels);
    }
}
