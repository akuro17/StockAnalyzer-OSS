using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Services;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingClipboardServiceTests
{
    private sealed class InMemoryClipboardService : IClipboardService
    {
        public string? Content { get; set; }

        public Task SetTextAsync(string text)
        {
            Content = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync() => Task.FromResult(Content);
    }

    private sealed class TestCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 1000;
        public double CanvasHeight => 800;
        public Rect ScreenRect => new Rect(0, 0, 1000, 800);
        public double ViewportX => 0;
        public double ViewportWidth => 1000;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 800.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(800.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 800.0 - (double)price;
    }

    [Fact]
    public async Task CopyAsync_ValidSelection_SerializesCustomJsonPayload()
    {
        var manager = new ChartObjectManager();
        var clipboard = new InMemoryClipboardService();

        var t1 = new DateTime(2025, 1, 10);
        var t2 = new DateTime(2025, 1, 20);
        var line = new TrendLineObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m))
        {
            Color = Colors.Red,
            Thickness = 2.5
        };
        var rect = new RectangleObject(new ChartPoint(t1, 50m), new ChartPoint(t2, 80m));

        manager.AddObject(line);
        manager.AddObject(rect);

        // Select both
        manager.SelectObjects(new[] { line.Id, rect.Id });

        await DrawingClipboardService.CopyAsync(
            manager.SelectedObjects.ToList(),
            ChartDrawingContextType.Standard,
            "7203",
            TimeframeType.Daily,
            manager,
            clipboard);

        Assert.NotNull(clipboard.Content);
        Assert.Contains(DrawingClipboardPayload.SchemaVersion, clipboard.Content);
        Assert.Contains("7203", clipboard.Content);

        var payload = JsonSerializer.Deserialize<DrawingClipboardPayload>(clipboard.Content!, DrawingDocumentCodec.SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal("7203", payload.SourceTicker);
        Assert.Equal(TimeframeType.Daily, payload.SourceTimeframe);
        Assert.Equal(2, payload.Objects.Count);
        Assert.Contains(payload.Objects, o => o.TypeName == nameof(TrendLineObject));
        Assert.Contains(payload.Objects, o => o.TypeName == nameof(RectangleObject));
    }

    [Fact]
    public async Task TryPasteAsync_ValidPayload_PastesClonesWithUniqueGuidsAndCorrectOffset()
    {
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var clipboard = new InMemoryClipboardService();
        var transform = new TestCoordinateTransform();

        var t1 = new DateTime(2025, 1, 10);
        var t2 = new DateTime(2025, 1, 20);
        var originalLine = new TrendLineObject(new ChartPoint(t1, 100m), new ChartPoint(t2, 200m));
        vm.ObjectManager.AddObject(originalLine);
        vm.ObjectManager.SelectObject(originalLine.Id);

        // Copy to clipboard
        await DrawingClipboardService.CopyAsync(
            vm.ObjectManager.SelectedObjects.ToList(),
            ChartDrawingContextType.Standard,
            vm.Symbol,
            vm.SelectedTimeFrame,
            vm.ObjectManager,
            clipboard);

        // Target paste position at mouse screen (400, 300)
        var mouseScreenPos = new Point(400, 300);
        var expectedTargetChart = transform.ScreenToChart(mouseScreenPos);

        bool success = await DrawingClipboardService.TryPasteAsync(vm, mouseScreenPos, transform, clipboard);

        Assert.True(success);
        Assert.Equal(2, vm.ObjectManager.Objects.Count);

        var pastedLine = vm.ObjectManager.Objects.FirstOrDefault(o => o.Id != originalLine.Id);
        Assert.NotNull(pastedLine);
        Assert.NotEqual(originalLine.Id, pastedLine.Id);

        // Verify pasted object is selected
        Assert.True(pastedLine.IsSelected);
        Assert.False(originalLine.IsSelected);

        // Verify reference anchor projected accurately
        // Original anchor was midpoint: ( (t1+t2)/2 = Jan 15, (100+200)/2 = 150m )
        var originalMidPrice = 150m;
        var deltaPrice = expectedTargetChart.Price - originalMidPrice;
        Assert.Equal(originalLine.Points[0].Price + deltaPrice, pastedLine.Points[0].Price);
    }

    [Fact]
    public async Task TryPasteAsync_WithLinkGroups_ReconstructsLinkGroupsWithMappedGuids()
    {
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var clipboard = new InMemoryClipboardService();
        var transform = new TestCoordinateTransform();

        var t1 = new DateTime(2025, 1, 10);
        var parentLine = new TrendLineObject(new ChartPoint(t1, 100m), new ChartPoint(t1.AddDays(5), 150m));
        var childText = new TextObject(new ChartPoint(t1, 100m), "Linked Note");

        vm.ObjectManager.AddObject(parentLine);
        vm.ObjectManager.AddObject(childText);
        vm.ObjectManager.LinkObjects(parentLine.Id, new[] { childText.Id });

        // Select both parent and child
        vm.ObjectManager.SelectObjects(new[] { parentLine.Id, childText.Id });

        await DrawingClipboardService.CopyAsync(
            vm.ObjectManager.SelectedObjects.ToList(),
            ChartDrawingContextType.Standard,
            vm.Symbol,
            vm.SelectedTimeFrame,
            vm.ObjectManager,
            clipboard);

        bool success = await DrawingClipboardService.TryPasteAsync(vm, new Point(200, 200), transform, clipboard);
        Assert.True(success);

        var groups = vm.ObjectManager.GetLinkGroupRecords(ChartDrawingContextType.Standard);
        Assert.Equal(2, groups.Count); // Original group + Pasted group

        var newGroup = groups.FirstOrDefault(g => g.ParentObjectId != parentLine.Id);
        Assert.NotNull(newGroup);
        Assert.Single(newGroup.ChildObjectIds);
        Assert.NotEqual(childText.Id, newGroup.ChildObjectIds[0]);

        // Ensure new parent and new child exist in ObjectManager
        Assert.NotNull(vm.ObjectManager.GetObject(newGroup.ParentObjectId));
        Assert.NotNull(vm.ObjectManager.GetObject(newGroup.ChildObjectIds[0]));
    }

    [Fact]
    public async Task CutAsync_DeletesUnlockedObjectsAndCopiesToClipboard_UndoRestoresState()
    {
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var clipboard = new InMemoryClipboardService();
        var transform = new TestCoordinateTransform();

        var t1 = new DateTime(2025, 1, 10);
        var unlocked = new TrendLineObject(new ChartPoint(t1, 100m), new ChartPoint(t1.AddDays(2), 120m)) { IsLocked = false };
        var locked = new RectangleObject(new ChartPoint(t1, 50m), new ChartPoint(t1.AddDays(2), 60m)) { IsLocked = true };

        vm.ObjectManager.AddObject(unlocked);
        vm.ObjectManager.AddObject(locked);

        vm.ObjectManager.SelectObjects(new[] { unlocked.Id, locked.Id });

        await DrawingClipboardService.CutAsync(vm, transform, clipboard);

        // Clipboard should contain both objects
        Assert.NotNull(clipboard.Content);
        var payload = JsonSerializer.Deserialize<DrawingClipboardPayload>(clipboard.Content!, DrawingDocumentCodec.SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Objects.Count);

        // Unlocked object must be removed, locked object must remain protected
        Assert.Null(vm.ObjectManager.GetObject(unlocked.Id));
        Assert.NotNull(vm.ObjectManager.GetObject(locked.Id));

        // Atomic Undo restores unlocked object
        Assert.True(vm.DrawingEditCoordinator?.CanUndo);
        var undoResult = vm.DrawingEditCoordinator!.Undo();
        Assert.True(undoResult.IsSuccess);
        Assert.Equal(2, vm.ObjectManager.Objects.Count);
        Assert.Contains(vm.ObjectManager.Objects, o => o is TrendLineObject);
        Assert.Contains(vm.ObjectManager.Objects, o => o is RectangleObject);
    }

    [Fact]
    public void DuplicateSelected_DuplicatesInPlace_PreservesExternalClipboard()
    {
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var transform = new TestCoordinateTransform();

        var original = new TrendLineObject(new ChartPoint(new DateTime(2025, 1, 10), 100m), new ChartPoint(new DateTime(2025, 1, 15), 150m));
        vm.ObjectManager.AddObject(original);
        vm.ObjectManager.SelectObject(original.Id);

        DrawingClipboardService.DuplicateSelected(vm, transform);

        Assert.Equal(2, vm.ObjectManager.Objects.Count);
        var clone = vm.ObjectManager.Objects.FirstOrDefault(o => o.Id != original.Id);
        Assert.NotNull(clone);
        Assert.NotEqual(original.Id, clone.Id);
        Assert.True(clone.IsSelected);
        Assert.False(original.IsSelected);

        // Undo removes duplicated clone
        Assert.True(vm.DrawingEditCoordinator?.CanUndo);
        var undoResult = vm.DrawingEditCoordinator!.Undo();
        Assert.True(undoResult.IsSuccess);
        Assert.Single(vm.ObjectManager.Objects);
        var restored = vm.ObjectManager.Objects[0];
        Assert.IsType<TrendLineObject>(restored);
        Assert.Equal(original.Points[0].Price, restored.Points[0].Price);
    }

    [Fact]
    public async Task TryPasteAsync_PlainTextFallback_CreatesTextObject()
    {
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var clipboard = new InMemoryClipboardService();
        var transform = new TestCoordinateTransform();

        clipboard.Content = "Important Breakout Zone";

        var mousePos = new Point(350, 250);
        bool success = await DrawingClipboardService.TryPasteAsync(vm, mousePos, transform, clipboard);

        Assert.True(success);
        Assert.Single(vm.ObjectManager.Objects);

        var textObj = vm.ObjectManager.Objects[0] as TextObject;
        Assert.NotNull(textObj);
        Assert.Equal("Important Breakout Zone", textObj.Text);
        Assert.True(textObj.IsSelected);

        var expectedChartPoint = transform.ScreenToChart(mousePos);
        Assert.Equal(expectedChartPoint.Time, textObj.Points[0].Time);
        Assert.Equal(expectedChartPoint.Price, textObj.Points[0].Price);
    }

    [Fact]
    public async Task TryPasteAsync_CrossChartCentering_WhenSourceTickerDiffers()
    {
        var vm = new ChartViewModel { Symbol = "9984" }; // SoftBank
        vm.UpdateDrawingCoordinator();
        var clipboard = new InMemoryClipboardService();
        var transform = new TestCoordinateTransform();

        // Create payload originating from "7203" (Toyota)
        var sourceAnchor = new ChartPoint(new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc), 1000m);
        var pt1 = DrawingDocumentCodec.ConvertChartPointToStored(sourceAnchor, DrawingCoordinateKind.UtcTime);
        var pt2 = DrawingDocumentCodec.ConvertChartPointToStored(new ChartPoint(sourceAnchor.Time.AddDays(5), sourceAnchor.Price + 50m), DrawingCoordinateKind.UtcTime);

        var record = new DrawingObjectRecord(
            Guid.NewGuid(),
            nameof(TrendLineObject),
            PanelKey.Main,
            DrawingCoordinateKind.UtcTime,
            new[] { pt1, pt2 },
            new Dictionary<string, object?>()
        );

        var payload = new DrawingClipboardPayload(
            "7203",
            TimeframeType.Daily,
            ChartDrawingContextType.Standard,
            sourceAnchor,
            new[] { record },
            Array.Empty<DrawingLinkGroupRecord>()
        );

        clipboard.Content = JsonSerializer.Serialize(payload, DrawingDocumentCodec.SerializerOptions);

        bool success = await DrawingClipboardService.TryPasteAsync(vm, mouseScreenPos: null, transform, clipboard);

        Assert.True(success);
        Assert.Single(vm.ObjectManager.Objects);

        var pasted = vm.ObjectManager.Objects[0];
        // Center of viewport (CanvasWidth=1000 / 2 = 500, CanvasHeight=800 / 2 = 400)
        var centerChart = transform.ScreenToChart(new Point(500, 400));

        // The first point was exactly at sourceAnchor, so after centering projection it should be at centerChart!
        Assert.Equal(centerChart.Time, pasted.Points[0].Time);
        Assert.Equal(centerChart.Price, pasted.Points[0].Price);
    }

    [Fact]
    public void HandleKeyDown_CtrlD_TriggersDuplicationAtomically()
    {
        var controller = new ChartInteractionController();
        var vm = new ChartViewModel { Symbol = "7203" };
        vm.UpdateDrawingCoordinator();
        var transform = new TestCoordinateTransform();

        var original = new TrendLineObject(new ChartPoint(new DateTime(2025, 1, 10), 100m), new ChartPoint(new DateTime(2025, 1, 15), 150m));
        vm.ObjectManager.AddObject(original);
        vm.ObjectManager.SelectObject(original.Id);

        var ctrlDArgs = new KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent,
            Key = Key.D,
            KeyModifiers = KeyModifiers.Control
        };

        bool handled = controller.HandleKeyDown(ctrlDArgs, vm, coordinateTransform: transform);

        Assert.True(handled);
        Assert.Equal(2, vm.ObjectManager.Objects.Count);
        Assert.True(vm.DrawingEditCoordinator?.CanUndo);
    }
}
