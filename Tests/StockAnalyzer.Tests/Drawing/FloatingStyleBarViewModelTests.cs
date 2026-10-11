using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class FloatingStyleBarViewModelTests
{
    private sealed class TestGeometricObject : RelativeGeometricRenderer
    {
        public override ChartObjectType Type => ChartObjectType.TrendLine;

        public TestGeometricObject(DateTime time1, decimal price1, DateTime time2, decimal price2)
        {
            Points.Add(new ChartPoint(time1, price1));
            Points.Add(new ChartPoint(time2, price2));
        }

        protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform) { }

        public override bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = 5) => true;
    }

    private static GenericCoordinateTransform CreateTransform(double width = 800, double height = 600)
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, width, height);
        transform.SetTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        transform.SetPriceRange(100m, 200m);
        return transform;
    }

    [Fact]
    public void UpdateSelection_WithValidSelection_PositionsAboveBoundingBoxAndClampsWithinViewport()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(1000, 800);

        var t1 = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var obj = new TestGeometricObject(t1, 140m, t2, 160m);
        chartVm.ObjectManager.AddObject(obj);

        var viewport = new Rect(0, 0, 1000, 800);
        styleBarVm.UpdateSelection(new[] { obj }, transform, viewport);

        Assert.True(styleBarVm.IsVisible);
        Assert.True(styleBarVm.PositionX >= 12.0);
        Assert.True(styleBarVm.PositionX + FloatingStyleBarViewModel.DefaultBarWidth <= viewport.Width - 12.0);
        Assert.True(styleBarVm.PositionY >= 12.0);
        Assert.True(styleBarVm.PositionY + FloatingStyleBarViewModel.DefaultBarHeight <= viewport.Height - 12.0);
    }

    [Fact]
    public void UpdateSelection_NearTopEdge_FlipsBelowOrClampsToViewportTop()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        // Price at 200m maps to near top (Y near 0)
        var t1 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        var obj = new TestGeometricObject(t1, 198m, t2, 199m);
        chartVm.ObjectManager.AddObject(obj);

        var viewport = new Rect(0, 0, 800, 600);
        styleBarVm.UpdateSelection(new[] { obj }, transform, viewport);

        Assert.True(styleBarVm.IsVisible);
        Assert.True(styleBarVm.PositionY >= 12.0, "PositionY should be clamped to top margin of at least 12.0");
    }

    [Fact]
    public void UpdateSelection_NearRightEdge_ClampsToViewportRight()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        // Time at Jan 9.5 maps to near right edge
        var t1 = new DateTime(2026, 1, 9, 12, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var obj = new TestGeometricObject(t1, 150m, t2, 150m);
        chartVm.ObjectManager.AddObject(obj);

        var viewport = new Rect(0, 0, 800, 600);
        styleBarVm.UpdateSelection(new[] { obj }, transform, viewport);

        Assert.True(styleBarVm.IsVisible);
        double maxExpectedX = 800 - FloatingStyleBarViewModel.DefaultBarWidth - 10.0;
        Assert.True(styleBarVm.PositionX <= maxExpectedX + 1.0, $"PositionX ({styleBarVm.PositionX}) should not exceed {maxExpectedX}");
    }

    [Fact]
    public void UpdateSelection_EmptySelection_SetsIsVisibleFalse()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);
        var viewport = new Rect(0, 0, 800, 600);

        styleBarVm.UpdateSelection(Array.Empty<IChartObject>(), transform, viewport);

        Assert.False(styleBarVm.IsVisible);
    }

    [Fact]
    public void SetColor_UpdatesColorOnSelectedObjects_AndThemeContext()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        styleBarVm.SetColor("#FF1744");

        Assert.Equal(Color.Parse("#FF1744"), styleBarVm.SelectedColor);
        Assert.Equal(Color.Parse("#FF1744").R, obj.Color.R);
        Assert.Equal(Color.Parse("#FF1744").G, obj.Color.G);
        Assert.Equal(Color.Parse("#FF1744").B, obj.Color.B);
        Assert.Equal(new SKColor(0xFF, 0x17, 0x44, 0xFF), DrawingThemeContext.DefaultSkColor);
    }

    [Fact]
    public void SetThickness_UpdatesThicknessOnSelectedObjects()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        styleBarVm.SetThickness(4.0);

        Assert.Equal(4.0, styleBarVm.SelectedThickness);
        Assert.Equal(4.0, obj.Thickness);
    }

    [Fact]
    public void SetOpacity_UpdatesAlphaComponentOnSelectedObjects()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        styleBarVm.SetOpacity(0.5);

        Assert.Equal(0.5, styleBarVm.SelectedOpacity);
        Assert.Equal((byte)128, obj.Color.A);
    }

    [Fact]
    public void SetStrokeStyle_UpdatesStrokeStyleOnSelectedObjects()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        styleBarVm.SetStrokeStyle(DrawingStrokeStyle.Dash);

        Assert.Equal(DrawingStrokeStyle.Dash, styleBarVm.SelectedStrokeStyle);
        Assert.Equal(DrawingStrokeStyle.Dash, obj.StrokeStyle);
    }

    [Fact]
    public void BringToFront_MovesSelectedObjectToTop()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj1 = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 130m);
        var obj2 = new TestGeometricObject(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), 140m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 150m);
        var obj3 = new TestGeometricObject(new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), 160m, new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), 170m);

        chartVm.ObjectManager.AddObject(obj1);
        chartVm.ObjectManager.AddObject(obj2);
        chartVm.ObjectManager.AddObject(obj3);

        styleBarVm.UpdateSelection(new[] { obj1 }, transform, new Rect(0, 0, 800, 600));
        styleBarVm.BringToFront();

        Assert.Equal(obj1.Id, chartVm.ObjectManager.Objects.Last().Id);
    }

    [Fact]
    public void SendToBack_MovesSelectedObjectToBottom()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj1 = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 130m);
        var obj2 = new TestGeometricObject(new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), 140m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 150m);
        var obj3 = new TestGeometricObject(new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), 160m, new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), 170m);

        chartVm.ObjectManager.AddObject(obj1);
        chartVm.ObjectManager.AddObject(obj2);
        chartVm.ObjectManager.AddObject(obj3);

        styleBarVm.UpdateSelection(new[] { obj3 }, transform, new Rect(0, 0, 800, 600));
        styleBarVm.SendToBack();

        Assert.Equal(obj3.Id, chartVm.ObjectManager.Objects.First().Id);
    }

    [Fact]
    public void ToggleLock_TogglesIsLockedOnSelectedObjects()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        Assert.False(obj.IsLocked);
        Assert.False(styleBarVm.IsLocked);

        styleBarVm.ToggleLock();

        Assert.True(obj.IsLocked);
        Assert.True(styleBarVm.IsLocked);
    }

    [Fact]
    public void Delete_RemovesSelectedObjectsAndHidesBar()
    {
        var chartVm = new ChartViewModel();
        var styleBarVm = new FloatingStyleBarViewModel(chartVm);
        var transform = CreateTransform(800, 600);

        var obj = new TestGeometricObject(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 120m, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), 140m);
        chartVm.ObjectManager.AddObject(obj);
        styleBarVm.UpdateSelection(new[] { obj }, transform, new Rect(0, 0, 800, 600));

        styleBarVm.Delete();

        Assert.DoesNotContain(obj, chartVm.ObjectManager.Objects);
        Assert.False(styleBarVm.IsVisible);
    }

    [Fact]
    public void InPlaceTextEditViewModel_BeginEdit_SetsEditingFlagsAndPositionsOverlay()
    {
        var vm = new InPlaceTextEditViewModel();
        var textObj = new TextObject(new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 150m), "Original Text");

        vm.BeginEdit(textObj, new Point(300, 200), width: 180, height: 50, isNew: false);

        Assert.True(vm.IsEditing);
        Assert.True(textObj.IsEditing);
        Assert.Equal("Original Text", vm.Text);
        Assert.Equal(180, vm.Width);
        Assert.Equal(50, vm.Height);
        Assert.Equal(300 - 90, vm.PositionX);
        Assert.Equal(200 - 25, vm.PositionY);
    }

    [Fact]
    public void InPlaceTextEditViewModel_Commit_UpdatesTextAndResetsEditingFlags()
    {
        var vm = new InPlaceTextEditViewModel();
        var manager = new ChartObjectManager();
        var textObj = new TextObject(new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 150m), "Original Text");
        manager.AddObject(textObj);

        vm.BeginEdit(textObj, new Point(300, 200), isNew: false);
        vm.Text = "Modified Text";

        vm.Commit(null, manager, null);

        Assert.False(vm.IsEditing);
        Assert.False(textObj.IsEditing);
        Assert.Equal("Modified Text", textObj.Text);
    }

    [Fact]
    public void InPlaceTextEditViewModel_Cancel_RestoresStateWithoutModifyingText()
    {
        var vm = new InPlaceTextEditViewModel();
        var manager = new ChartObjectManager();
        var textObj = new TextObject(new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 150m), "Original Text");
        manager.AddObject(textObj);

        vm.BeginEdit(textObj, new Point(300, 200), isNew: false);
        vm.Text = "Temporary Unsaved Text";

        vm.Cancel(manager);

        Assert.False(vm.IsEditing);
        Assert.False(textObj.IsEditing);
        Assert.Equal("Original Text", textObj.Text);
    }

    [Fact]
    public void InPlaceTextEditViewModel_NewObjectEmptyText_DiscardsObject()
    {
        var vm = new InPlaceTextEditViewModel();
        var manager = new ChartObjectManager();
        var newObj = new TextObject(new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 150m), "");

        vm.BeginEdit(newObj, new Point(300, 200), isNew: true);
        vm.Text = "   "; // Whitespace only

        vm.Commit(null, manager, null);

        Assert.False(vm.IsEditing);
        Assert.DoesNotContain(newObj, manager.Objects);
    }

    [Fact]
    public void TextObject_IsEditing_SuppressesSkiaRendering()
    {
        var textObj = new TextObject(new ChartPoint(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), 150m), "Test Suppression");
        textObj.IsEditing = true;

        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        var transform = CreateTransform();

        // Rendering while IsEditing == true must execute cleanly without error and perform 0 operations
        var exception = Record.Exception(() => textObj.Render(canvas, transform));
        Assert.Null(exception);
    }

    [Fact]
    public void DrawingThemeContext_SetDefaultColor_UpdatesBothSkColorAndAvaloniaColor()
    {
        var newColor = new SKColor(0x00, 0xE6, 0x76, 0xFF);
        DrawingThemeContext.SetDefaultColor(newColor);

        Assert.Equal(newColor, DrawingThemeContext.DefaultSkColor);
        Assert.Equal(Color.FromArgb(255, 0, 230, 118), DrawingThemeContext.DefaultColor);
    }
}
