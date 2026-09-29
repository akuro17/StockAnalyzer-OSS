using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// T4 of "SubWindow Drawing Tools": ChartObjectManager partitions render + hit-test by PanelIndex,
/// and SubWindowTransformCoordinator hands out one panel-local transform per allocated sub-window panel.
/// </summary>
public class SubWindowRenderPassTests
{
    private sealed class FakeChartObject : IChartObject
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ChartObjectType Type { get; set; } = ChartObjectType.TrendLine;
        public string? CustomName { get; set; }
        public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
        public bool IsMoveAxisModeExplicit { get; set; }
        public List<ChartPoint> Points { get; set; } = new();
        public Color Color { get; set; } = Colors.Black;
        public double Thickness { get; set; } = 1.0;
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; }
        public int ZIndex { get; set; }
        public int PanelIndex { get; set; } = -1;
        public SKColor SkiaColor => SKColors.Black;

        public int RenderCount { get; private set; }
        public ICoordinateTransform? LastRenderTransform { get; private set; }
        public bool HitResult { get; set; } = true;

        public void Render(SKCanvas canvas, ICoordinateTransform transform)
        {
            RenderCount++;
            LastRenderTransform = transform;
        }

        public bool HitTest(Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
            => HitResult;

        public void Translate(TimeSpan timeDelta, decimal priceDelta) { }
    }

    private static SKCanvas NewCanvas() => new(new SKBitmap(64, 64));

    [Fact]
    public void RenderPanel_OnlyRendersObjectsBelongingToThatPanel()
    {
        var manager = new ChartObjectManager();
        var main = new FakeChartObject { PanelIndex = -1 };
        var panel0 = new FakeChartObject { PanelIndex = 0 };
        var panel1 = new FakeChartObject { PanelIndex = 1 };
        manager.AddObject(main);
        manager.AddObject(panel0);
        manager.AddObject(panel1);

        using var canvas = NewCanvas();
        manager.RenderPanel(canvas, 0, null!);

        Assert.Equal(0, main.RenderCount);
        Assert.Equal(1, panel0.RenderCount);
        Assert.Equal(0, panel1.RenderCount);
    }

    [Fact]
    public void Render_IsEquivalentToRenderPanelMinusOne()
    {
        var manager = new ChartObjectManager();
        var main = new FakeChartObject { PanelIndex = -1 };
        var panel0 = new FakeChartObject { PanelIndex = 0 };
        manager.AddObject(main);
        manager.AddObject(panel0);

        using var canvas = NewCanvas();
        manager.Render(canvas, null!);

        Assert.Equal(1, main.RenderCount);
        Assert.Equal(0, panel0.RenderCount);
    }

    [Fact]
    public void RenderPanel_SkipsInvisibleObjects()
    {
        var manager = new ChartObjectManager();
        var visible = new FakeChartObject { PanelIndex = 0, IsVisible = true };
        var hidden = new FakeChartObject { PanelIndex = 0, IsVisible = false };
        manager.AddObject(visible);
        manager.AddObject(hidden);

        using var canvas = NewCanvas();
        manager.RenderPanel(canvas, 0, null!);

        Assert.Equal(1, visible.RenderCount);
        Assert.Equal(0, hidden.RenderCount);
    }

    [Fact]
    public void GetObjectAt_WithTargetPanelIndex_IgnoresObjectsInOtherPanels()
    {
        var manager = new ChartObjectManager();
        var panel0 = new FakeChartObject { PanelIndex = 0 };
        var panel1 = new FakeChartObject { PanelIndex = 1 };
        manager.AddObject(panel0);
        manager.AddObject(panel1);

        Assert.Same(panel1, manager.GetObjectAt(new Point(1, 1), null!, targetPanelIndex: 1));
        Assert.Same(panel0, manager.GetObjectAt(new Point(1, 1), null!, targetPanelIndex: 0));
        Assert.Null(manager.GetObjectAt(new Point(1, 1), null!, targetPanelIndex: 2));
    }

    [Fact]
    public void GetObjectAt_WithoutTargetPanelIndex_PreservesLegacyBehavior_AndConsidersAllPanels()
    {
        var manager = new ChartObjectManager();
        var panel0 = new FakeChartObject { PanelIndex = 0 };
        var panel1 = new FakeChartObject { PanelIndex = 1 };
        manager.AddObject(panel0);
        manager.AddObject(panel1);

        // Top-most (last added) wins when no panel filter is supplied.
        Assert.Same(panel1, manager.GetObjectAt(new Point(1, 1), null!));
    }

    // ---- SubWindowTransformCoordinator ----

    private static ChartDataSnapshot SnapshotWith(params CoreIndicatorSettings[] settings)
    {
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2024, 1, 1), 10m, 12m, 9m, 11m, 100L),
            new(new DateTime(2024, 1, 2), 11m, 13m, 10m, 12m, 120L),
        };
        return new ChartDataSnapshot(candles, indicatorSettings: settings);
    }

    private static CoreIndicatorSettings SubPanelIndicator(string id, decimal min, decimal max) => new()
    {
        Id = id,
        IsEnabled = true,
        IsOverlay = false,
        TypeEnum = IndicatorType.RSI,
        MinValue = min,
        MaxValue = max,
    };

    private static ChartLayoutContext LayoutWithPanels(int panelCount)
    {
        var panels = new List<Rect>();
        for (int i = 0; i < panelCount; i++)
            panels.Add(new Rect(50, 400 + i * 100, 600, 100));
        return new ChartLayoutContext(
            new Rect(0, 0, 700, 800),
            new Rect(50, 0, 600, 400),
            new Rect(0, 0, 0, 0),
            panels,
            0, 0, 50, 50);
    }

    private static GenericCoordinateTransform MainTransform()
    {
        var t = new GenericCoordinateTransform(ChartAxisMode.Time, 600, 400);
        t.SetTimeRange(new DateTime(2024, 1, 1), new DateTime(2024, 1, 2));
        t.SetPriceRange(0m, 200m);
        return t;
    }

    [Fact]
    public void Coordinator_AllocatesOneTransformPerSubWindowPanel_InSettingsOrder()
    {
        var coord = new SubWindowTransformCoordinator();
        var snapshot = SnapshotWith(
            SubPanelIndicator("rsi", 0m, 100m),
            SubPanelIndicator("macd", -5m, 5m));

        coord.UpdateTransforms(LayoutWithPanels(2), snapshot, MainTransform(), isSubWindowVisible: true);

        Assert.NotNull(coord.GetTransform(0));
        Assert.NotNull(coord.GetTransform(1));
        Assert.Null(coord.GetTransform(2));
    }

    [Fact]
    public void Coordinator_PanelTransform_MapsPriceWithinThePanelHeight_AndRoundTrips()
    {
        var coord = new SubWindowTransformCoordinator();
        var snapshot = SnapshotWith(SubPanelIndicator("rsi", 0m, 100m));

        coord.UpdateTransforms(LayoutWithPanels(1), snapshot, MainTransform(), isSubWindowVisible: true);
        var t = coord.GetTransform(0)!;

        // A price in the middle of the RSI range maps near the vertical middle of the 100px panel...
        var mid = t.ChartToScreen(new ChartPoint(new DateTime(2024, 1, 1, 12, 0, 0), 50m));
        Assert.InRange(mid.Y, 5.0, 95.0);

        // ...and ScreenToChart inverts ChartToScreen for both axes.
        var rt = t.ScreenToChart(mid);
        Assert.Equal(50m, rt.Price, precision: 2);
    }

    [Fact]
    public void Coordinator_WhenSubWindowHidden_ClearsAllTransforms()
    {
        var coord = new SubWindowTransformCoordinator();
        var snapshot = SnapshotWith(SubPanelIndicator("rsi", 0m, 100m));

        coord.UpdateTransforms(LayoutWithPanels(1), snapshot, MainTransform(), isSubWindowVisible: true);
        Assert.NotNull(coord.GetTransform(0));

        coord.UpdateTransforms(LayoutWithPanels(1), snapshot, MainTransform(), isSubWindowVisible: false);
        Assert.Null(coord.GetTransform(0));
    }

    [Fact]
    public void Coordinator_PrunesStaleTransforms_WhenPanelCountDecreases()
    {
        var coord = new SubWindowTransformCoordinator();
        var twoPanels = SnapshotWith(
            SubPanelIndicator("rsi", 0m, 100m),
            SubPanelIndicator("macd", -5m, 5m));

        coord.UpdateTransforms(LayoutWithPanels(2), twoPanels, MainTransform(), isSubWindowVisible: true);
        Assert.NotNull(coord.GetTransform(1));

        var onePanel = SnapshotWith(SubPanelIndicator("rsi", 0m, 100m));
        coord.UpdateTransforms(LayoutWithPanels(1), onePanel, MainTransform(), isSubWindowVisible: true);

        Assert.NotNull(coord.GetTransform(0));
        Assert.Null(coord.GetTransform(1));
    }

    private static ChartDataSnapshot SnapshotWithN(int n)
    {
        var s = new CoreIndicatorSettings[n];
        for (int i = 0; i < n; i++) s[i] = SubPanelIndicator("ind" + i, 0m, 100m);
        return SnapshotWith(s);
    }

    /// <summary>
    /// P1 regression ("SubWindow Drawing Tools — coordinate fixes", take 3): the render thread rebuilds
    /// the panel-transform maps every compositor frame via <see cref="SubWindowTransformCoordinator.UpdateTransforms"/>
    /// (ChartRenderPipeline step 7.5), while the UI thread reads them on every pointer move via
    /// <see cref="SubWindowTransformCoordinator.GetTransform"/> (ChartBaseControl.ResolvePointerTarget).
    /// Before the fix these ran unsynchronized against a plain <see cref="Dictionary{TKey,TValue}"/>,
    /// so a lookup landing mid-rebuild corrupted the bucket state and threw
    /// <see cref="InvalidOperationException"/> ("a concurrent update was performed on this collection")
    /// — or silently returned another panel's transform, mapping a sub-panel series with the wrong
    /// Y-scale (the phantom short vertical line in the squeezed volume sub-panel, at an arbitrary X,
    /// only while the mouse moves and only with enough sub-panels). With the lock every entry point
    /// has exclusive access, so hammering rebuild + lookup from several threads completes cleanly.
    /// </summary>
    [Fact]
    public void Coordinator_ConcurrentRebuildAndLookup_DoesNotCorruptThePanelMap()
    {
        // Deterministic by construction: the work is counted in iterations (not a wall-clock deadline, which let a reader that the
        // thread pool started late finish with zero lookups under load), every participant runs on its own dedicated thread, and all
        // of them are released together by one barrier so the rebuild and the lookups genuinely overlap.
        const int MaxPanels = 12;
        const int ReaderCount = 3;
        const int ReaderPasses = 20_000;
        var coord = new SubWindowTransformCoordinator();
        var main = MainTransform();
        var big = SnapshotWithN(MaxPanels);
        var small = SnapshotWithN(2);

        coord.UpdateTransforms(LayoutWithPanels(MaxPanels), big, main, isSubWindowVisible: true);

        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new System.Threading.Barrier(ReaderCount + 1);
        int readersRunning = ReaderCount;

        var writer = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                start.SignalAndWait();
                int i = 0;
                while (System.Threading.Volatile.Read(ref readersRunning) > 0)
                {
                    // Cycle visible(many) -> visible(few) -> hidden so every pass exercises
                    // insert, Remove() pruning and Clear() against the concurrent readers.
                    switch (i++ % 3)
                    {
                        case 0: coord.UpdateTransforms(LayoutWithPanels(MaxPanels), big, main, isSubWindowVisible: true); break;
                        case 1: coord.UpdateTransforms(LayoutWithPanels(2), small, main, isSubWindowVisible: true); break;
                        default: coord.UpdateTransforms(LayoutWithPanels(MaxPanels), big, main, isSubWindowVisible: false); break;
                    }
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }, System.Threading.Tasks.TaskCreationOptions.LongRunning);

        var readers = new System.Threading.Tasks.Task[ReaderCount];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int pass = 0; pass < ReaderPasses; pass++)
                    {
                        for (int k = 0; k < MaxPanels + 2; k++)
                        {
                            // Mirrors ChartBaseControl.ResolvePointerTarget probing the panel map on
                            // every pointer move. A concurrent rebuild must never corrupt the lookup.
                            _ = coord.GetTransform(k);
                        }
                    }
                }
                catch (Exception ex) { failures.Enqueue(ex); }
                finally { System.Threading.Interlocked.Decrement(ref readersRunning); }
            }, System.Threading.Tasks.TaskCreationOptions.LongRunning);
        }

        System.Threading.Tasks.Task.WaitAll(new[] { writer }.Concat(readers).ToArray());

        Assert.Empty(failures);

        // The map must still be intact and usable after the hammering (a corrupted dictionary would lose or mix entries).
        coord.UpdateTransforms(LayoutWithPanels(MaxPanels), big, main, isSubWindowVisible: true);
        for (int k = 0; k < MaxPanels; k++) Assert.NotNull(coord.GetTransform(k));
        Assert.Null(coord.GetTransform(MaxPanels));
    }
}
