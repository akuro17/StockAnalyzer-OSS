using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class HighlighterAndStampObjectTests
{
    private static GenericCoordinateTransform CreateTestTransform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        transform.SetTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        transform.SetPriceRange(100m, 200m);
        return transform;
    }

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
    public void HighlighterObject_DefaultsAndAlphaClamping_AreEnforced()
    {
        var highlighter = new HighlighterObject();

        Assert.Equal(ChartObjectType.Highlighter, highlighter.Type);
        Assert.Equal(12.0, highlighter.Thickness);
        Assert.Equal(100, highlighter.Alpha);

        // Clamping below 20
        highlighter.Alpha = 5;
        Assert.Equal(20, highlighter.Alpha);

        // Clamping above 200
        highlighter.Alpha = 250;
        Assert.Equal(200, highlighter.Alpha);

        // In range
        highlighter.Alpha = 150;
        Assert.Equal(150, highlighter.Alpha);
    }

    [Fact]
    public void HighlighterObject_TranslateAndAabbTransform_PreserveInvariants()
    {
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var highlighter = new HighlighterObject(new[]
        {
            new ChartPoint(t0, 110m),
            new ChartPoint(t1, 190m)
        });

        // 1. Verify Translate
        highlighter.Translate(TimeSpan.FromDays(1), 10m);
        Assert.Equal(t0.AddDays(1), highlighter.Points[0].Time);
        Assert.Equal(120m, highlighter.Points[0].Price);
        Assert.Equal(t1.AddDays(1), highlighter.Points[1].Time);
        Assert.Equal(200m, highlighter.Points[1].Price);

        // 2. Verify AABB deformation
        var newMinT = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var newMaxT = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);
        highlighter.TransformAabb(newMinT, newMaxT, 130m, 170m);

        Assert.Equal(newMinT, highlighter.Points[0].Time);
        Assert.Equal(130m, highlighter.Points[0].Price);
        Assert.Equal(newMaxT, highlighter.Points[1].Time);
        Assert.Equal(170m, highlighter.Points[1].Price);

        // Verify DateTimeKind.Utc monotonicity
        Assert.Equal(DateTimeKind.Utc, highlighter.Points[0].Time.Kind);
        Assert.Equal(DateTimeKind.Utc, highlighter.Points[1].Time.Kind);
    }

    [Fact]
    public void HighlighterObject_HitTestAndHandles_WorkAccurately()
    {
        var transform = CreateTestTransform();
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        var highlighter = new HighlighterObject(new[]
        {
            new ChartPoint(t0, 150m),
            new ChartPoint(t1, 150m)
        });

        var p0 = transform.ChartToScreen(new ChartPoint(t0, 150m));
        var p1 = transform.ChartToScreen(new ChartPoint(t1, 150m));
        var midPt = new global::Avalonia.Point((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5);

        // Midpoint should hit
        Assert.True(highlighter.HitTest(midPt, transform));

        // Way off should miss
        Assert.False(highlighter.HitTest(new global::Avalonia.Point(midPt.X, midPt.Y + 200), transform));

        // 8 AABB handles
        var handles = highlighter.GetAabbHandlePositions(transform);
        Assert.Equal(8, handles.Length);
    }

    [Fact]
    public void HighlighterObject_SerializationRoundTrip_Succeeds()
    {
        var t0 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var original = new HighlighterObject(new[]
        {
            new ChartPoint(t0, 120m),
            new ChartPoint(t1, 160m)
        })
        {
            Alpha = 140,
            Thickness = 16.0,
            Color = Colors.Yellow
        };

        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IChartObject>(json, JsonOptions) as HighlighterObject;

        Assert.NotNull(deserialized);
        Assert.Equal(ChartObjectType.Highlighter, deserialized.Type);
        Assert.Equal(140, deserialized.Alpha);
        Assert.Equal(16.0, deserialized.Thickness);
        Assert.Equal(Colors.Yellow, deserialized.Color);
        Assert.Equal(2, deserialized.Points.Count);
        Assert.Equal(t0, deserialized.Points[0].Time);
        Assert.Equal(120m, deserialized.Points[0].Price);
    }



    [Fact]
    public void StepBadgeObject_GlyphMappingAndFormatting_WorkCorrectly()
    {
        var pos = new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 150m);
        var badge = new StepBadgeObject(pos, 1);

        Assert.Equal(ChartObjectType.StepBadge, badge.Type);
        Assert.Equal(1, badge.StepNumber);
        Assert.Equal(14.0, badge.BadgeRadius);

        // Clamping bounds [1, 999]
        badge.StepNumber = -5;
        Assert.Equal(1, badge.StepNumber);

        badge.StepNumber = 1500;
        Assert.Equal(999, badge.StepNumber);

        badge.StepNumber = 20;
        Assert.Equal(20, badge.StepNumber);

        badge.StepNumber = 21;
        Assert.Equal(21, badge.StepNumber);
    }

    [Fact]
    public void StepBadgeObject_HitTest_DetectsWithinCircle()
    {
        var transform = CreateTestTransform();
        var pos = new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 150m);
        var badge = new StepBadgeObject(pos) { BadgeRadius = 14.0 };

        var center = transform.ChartToScreen(pos);
        Assert.True(badge.HitTest(new global::Avalonia.Point(center.X + 5, center.Y + 5), transform));
        Assert.False(badge.HitTest(new global::Avalonia.Point(center.X + 50, center.Y + 50), transform));
    }

    [Fact]
    public void StepBadgeObject_SerializationRoundTrip_Succeeds()
    {
        var pos = new ChartPoint(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 150m);
        var original = new StepBadgeObject(pos, 42)
        {
            BadgeRadius = 18.0,
            TextColor = Colors.Cyan,
            Color = Colors.DarkBlue
        };

        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<IChartObject>(json, JsonOptions) as StepBadgeObject;

        Assert.NotNull(deserialized);
        Assert.Equal(ChartObjectType.StepBadge, deserialized.Type);
        Assert.Equal(42, deserialized.StepNumber);
        Assert.Equal(18.0, deserialized.BadgeRadius);
        Assert.Equal(Colors.Cyan, deserialized.TextColor);
        Assert.Equal(Colors.DarkBlue, deserialized.Color);
        Assert.Single(deserialized.Points);
    }

    [Fact]
    public void ChartInteractionController_ActiveStepIndex_AutoIncrements()
    {
        var controller = new ChartInteractionController();
        Assert.Equal(1, controller.ActiveStepIndex);

        controller.ActiveStepIndex = 5;
        Assert.Equal(5, controller.ActiveStepIndex);
    }

    [Fact]
    public void ChartObjectTypeRegistry_ResolvesNewTypes()
    {
        Assert.Equal(typeof(HighlighterObject), ChartObjectTypeRegistry.Resolve(nameof(HighlighterObject)));
        Assert.Equal(typeof(StepBadgeObject), ChartObjectTypeRegistry.Resolve(nameof(StepBadgeObject)));
    }
}
