using System;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class InformationToolInteractionTests
{
    [Fact]
    public void DrawingToolCategoryService_CursorsCategory_ContainsInformationTool()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var cursorsCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Cursors");

        Assert.NotNull(cursorsCategory);
        var toolInfo = cursorsCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.Information);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u2139\uFE0F", toolInfo.Icon);
        Assert.Equal("DrawTool_Information", toolInfo.NameKey);
    }

    [Fact]
    public void ChartObjectManager_InformationObject_ProvidesO1CachedAccess()
    {
        var manager = new ChartObjectManager();
        Assert.Null(manager.InformationObject);

        using var infoObj1 = new InformationObject();
        infoObj1.Points.Add(new ChartPoint(new DateTime(2026, 1, 1), 100m));

        bool added1 = manager.AddObject(infoObj1);
        Assert.True(added1);
        Assert.Same(infoObj1, manager.InformationObject);

        // Attempting to add a second InformationObject is rejected
        using var infoObj2 = new InformationObject();
        infoObj2.Points.Add(new ChartPoint(new DateTime(2026, 1, 2), 110m));

        bool added2 = manager.AddObject(infoObj2);
        Assert.False(added2);
        Assert.Same(infoObj1, manager.InformationObject);

        // Removing the object clears the cached property
        bool removed = manager.RemoveObject(infoObj1.Id);
        Assert.True(removed);
        Assert.Null(manager.InformationObject);
    }

    [Fact]
    public void ChartObjectManager_AddInformationObject_EnforcesSingleInstance()
    {
        var manager = new ChartObjectManager();
        using var infoObj1 = new InformationObject();
        infoObj1.Points.Add(new ChartPoint(new DateTime(2026, 1, 1), 100m));

        bool added1 = manager.AddObject(infoObj1);
        Assert.True(added1);
        Assert.Single(manager.Objects);
        Assert.Equal(ChartObjectType.Information, manager.Objects[0].Type);

        // Adding a second InformationObject must be rejected (|S| <= 1)
        using var infoObj2 = new InformationObject();
        infoObj2.Points.Add(new ChartPoint(new DateTime(2026, 1, 2), 110m));

        bool added2 = manager.AddObject(infoObj2);
        Assert.False(added2);
        Assert.Single(manager.Objects);
        Assert.Same(infoObj1, manager.Objects[0]);

        // Removing the first object permits adding a new one
        bool removed = manager.RemoveObject(infoObj1.Id);
        Assert.True(removed);
        Assert.Empty(manager.Objects);

        bool addedAgain = manager.AddObject(infoObj2);
        Assert.True(addedAgain);
        Assert.Single(manager.Objects);
        Assert.Same(infoObj2, manager.Objects[0]);
    }

    [Fact]
    public void InformationObject_IndividualProperties_DefaultsAndOverrides()
    {
        using var infoObj = new InformationObject();

        Assert.Equal(ChartObjectType.Information, infoObj.Type);
        Assert.Equal(1.0, infoObj.Thickness);
        Assert.Equal(95, infoObj.FillOpacity);
        Assert.False(infoObj.HasCustomFillColor);
        Assert.False(infoObj.HasCustomFontColor);
        Assert.False(infoObj.HasCustomFontSize);

        // Verify custom individual overrides
        infoObj.Color = Colors.Magenta;
        infoObj.Thickness = 2.5;
        infoObj.FillColor = Colors.DarkSlateGray;
        infoObj.FillOpacity = 80;
        infoObj.FontColor = Colors.LightGoldenrodYellow;
        infoObj.FontSize = 14.5;

        Assert.Equal(Colors.Magenta, infoObj.Color);
        Assert.Equal(2.5, infoObj.Thickness);
        Assert.Equal(Colors.DarkSlateGray, infoObj.FillColor);
        Assert.True(infoObj.HasCustomFillColor);
        Assert.Equal(80, infoObj.FillOpacity);
        Assert.Equal(Colors.LightGoldenrodYellow, infoObj.FontColor);
        Assert.True(infoObj.HasCustomFontColor);
        Assert.Equal(14.5, infoObj.FontSize);
        Assert.True(infoObj.HasCustomFontSize);
    }

    [Fact]
    public void InformationSettingsPanelDefinition_CanHandle_MatchesInformationObjectOnly()
    {
        var definition = new InformationSettingsPanelDefinition();
        using var infoObj = new InformationObject();
        using var otherObj = new AngleObject(new ChartPoint(DateTime.Now, 100m), new ChartPoint(DateTime.Now, 110m));

        Assert.True(definition.CanHandle(infoObj));
        Assert.False(definition.CanHandle(otherObj));
    }

    [Fact]
    public void InformationObject_HitTest_DetectsCardBoundsAndAnchorMarker()
    {
        using var infoObj = new InformationObject();
        var point = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        infoObj.Points.Add(point);

        // Simulate rendered card bounds at top-left [10, 10, 210, 160]
        infoObj.LastRenderedBounds = new global::Avalonia.Rect(10, 10, 200, 150);

        var transform = new LinearCoordinateTransform(new DateTime(2026, 1, 1), new DateTime(2026, 1, 10), 50m, 150m, 800, 600);

        // 1. Inside card bounds -> Hit
        Assert.True(infoObj.HitTest(new global::Avalonia.Point(50, 50), transform));

        // 2. Far outside card and anchor -> Miss
        Assert.False(infoObj.HitTest(new global::Avalonia.Point(700, 500), transform));

        // 3. Near anchor point -> Hit
        var anchorScreen = transform.ChartToScreen(point);
        Assert.True(infoObj.HitTest(new global::Avalonia.Point(anchorScreen.X + 2, anchorScreen.Y + 2), transform));

        // 4. Hidden object -> Always miss
        infoObj.IsVisible = false;
        Assert.False(infoObj.HitTest(new global::Avalonia.Point(50, 50), transform));
    }

    [Fact]
    public void LayersPanel_DisplayName_ResolvesCorrectlyForInformationObject()
    {
        using var infoObj = new InformationObject();
        string name = DrawingObjectDisplayNameHelper.GetDisplayName(infoObj);
        Assert.False(string.IsNullOrWhiteSpace(name));

        // When CustomName is specified, it takes priority
        infoObj.CustomName = "Custom Info Badge";
        Assert.Equal("Custom Info Badge", DrawingObjectDisplayNameHelper.GetDisplayName(infoObj));
    }

    [Fact]
    public void InformationObject_DrawGeometry_PinsControlPointToTopLeftCorner()
    {
        using var bitmap = new SkiaSharp.SKBitmap(800, 600);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        var transform = new LinearCoordinateTransform(
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31),
            0m, 200m,
            800, 600);

        using var infoObj = new InformationObject();
        var time = new DateTime(2026, 1, 15);
        infoObj.Snapshot = new ChartInformationSnapshot(time, null, Array.Empty<IndicatorInformationItem>(), Array.Empty<DrawingInformationItem>());

        // Before render, Points may be empty
        Assert.Empty(infoObj.Points);

        // Render once
        infoObj.Render(canvas, transform);
        canvas.Flush();

        // Control point must be pinned to top-left corner
        Assert.Single(infoObj.Points);
        var controlPointScreen = transform.ChartToScreen(infoObj.Points[0]);

        Assert.True(Math.Abs(controlPointScreen.X - infoObj.LastRenderedBounds.X) < 1.0,
            $"Expected X close to {infoObj.LastRenderedBounds.X}, got {controlPointScreen.X}");
        Assert.True(Math.Abs(controlPointScreen.Y - infoObj.LastRenderedBounds.Y) < 1.0,
            $"Expected Y close to {infoObj.LastRenderedBounds.Y}, got {controlPointScreen.Y}");
        Assert.True(Math.Abs(controlPointScreen.X - 10f) < 1.0);
        Assert.True(Math.Abs(controlPointScreen.Y - 10f) < 1.0);

        // Re-rendering with another snapshot must keep the control point at top-left
        var time2 = new DateTime(2026, 1, 20);
        infoObj.Snapshot = new ChartInformationSnapshot(time2, null, Array.Empty<IndicatorInformationItem>(), Array.Empty<DrawingInformationItem>());
        infoObj.Render(canvas, transform);
        canvas.Flush();

        Assert.Single(infoObj.Points);
        var controlPointScreen2 = transform.ChartToScreen(infoObj.Points[0]);
        Assert.True(Math.Abs(controlPointScreen2.X - infoObj.LastRenderedBounds.X) < 1.0);
        Assert.True(Math.Abs(controlPointScreen2.Y - infoObj.LastRenderedBounds.Y) < 1.0);
    }

    [Fact]
    public void InformationObject_IsSelected_PreservesConfiguredColor()
    {
        using var infoObj = new InformationObject();
        infoObj.Color = Colors.DarkOrange;

        // Changing selection state to true
        infoObj.IsSelected = true;

        Assert.Equal(Colors.DarkOrange, infoObj.Color);
        Assert.Equal(new SkiaSharp.SKColor(Colors.DarkOrange.R, Colors.DarkOrange.G, Colors.DarkOrange.B, Colors.DarkOrange.A), infoObj.SkiaColor);
    }

    [Fact]
    public void InformationObject_Translate_IsNoOp()
    {
        using var infoObj = new InformationObject();
        var originalPoint = new ChartPoint(new DateTime(2026, 1, 1), 100m);
        infoObj.Points.Add(originalPoint);

        infoObj.Translate(TimeSpan.FromDays(5), 50m);

        Assert.Single(infoObj.Points);
        Assert.Equal(originalPoint.Time, infoObj.Points[0].Time);
        Assert.Equal(originalPoint.Price, infoObj.Points[0].Price);
    }
}
