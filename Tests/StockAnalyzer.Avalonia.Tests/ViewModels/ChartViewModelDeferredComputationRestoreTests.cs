using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Reproduces the reported bug: after restarting the app (i.e. restoring a drawing from a
/// pre-existing file, exactly as ChartDrawingPersistenceTests above does for a plain
/// TrendLineObject), a RangeSplineObject was effectively broken -- not rendered as a real
/// curve (only its Points[0] anchor showed), not selectable by clicking it, and therefore
/// not draggable or deletable via the normal click-to-select flow (the user had to resort
/// to Layer Management to delete it).
///
/// Root cause: RegressionTrendObject/RangeSplineObject/FixedRangeVolumeProfileObject all
/// defer their expensive-ish per-candle computation (regression fit / spline point
/// extraction / volume histogram) out of their constructors into a separate Recalculate()
/// call, which every OTHER code path (creation, handle-drag-then-release, move-drag-then-
/// release) remembers to call explicitly. ChartObjectManager.LoadSnapshot() -- used by
/// ChartViewModel.LoadDataInternalAsync() to restore persisted drawings on app start /
/// symbol switch -- only restores Points[] (via JSON deserialization) and never calls
/// Recalculate(), so a restored object's derived state (_extractedPoints for
/// RangeSplineObject) stays permanently empty until the user happens to drag it (which
/// finally triggers a Recalculate() as a side effect of the drag-release path).
/// RangeSplineObject.HitTest() explicitly early-returns false when _extractedPoints is
/// empty, which is what made the restored object unselectable (and therefore
/// undraggable/undeletable via the normal flow) until touched.
/// </summary>
public class ChartViewModelDeferredComputationRestoreTests : IDisposable
{
    private const string TickerRestore = "ZZTEST-RSPLN";
    private readonly ChartDrawingRepository _sharedRepository = new();
    private readonly ChartViewModel _sut;

    /// <summary>
    /// Returns candles with a CONSTANT known price, so a drawing object's Points[] can be
    /// seeded with that exact same price and be guaranteed to land on the real extracted
    /// curve -- removing "did my raw click price happen to match real data" as a
    /// confounding variable when testing selection/handle-drag precision.
    /// </summary>
    private sealed class FixedPriceDataService : IDataService
    {
        public const decimal FixedPrice = 100m;

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            var candles = new List<CandleData>();
            var now = DateTime.Now.Date;
            for (int i = 0; i < count; i++)
            {
                candles.Add(new CandleData(now.AddDays(-count + i), FixedPrice, FixedPrice, FixedPrice, FixedPrice, 1000));
            }
            return Task.FromResult<IReadOnlyList<CandleData>>(candles);
        }
    }

    public ChartViewModelDeferredComputationRestoreTests()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });
        var dataService = new FixedPriceDataService();

        _sut = new ChartViewModel(
            dataService,
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!, // strategyFactory
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Avalonia.Services.TimeFrameManager(dataService),
            null!, // marketStructureService
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,  // predictionService
            null!, // analysisPipelineService
            null,  // marketDataProvider
            null,  // pythonService
            null,  // comparisonDataAligner
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger(),
            drawingRepository: _sharedRepository
        );
    }

    public void Dispose()
    {
        _sut.Dispose();
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        var path = Path.Combine(dir, $"{TickerRestore}.{TimeframeType.Daily}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static Task SettleAsync() => Task.Delay(ChartConstants.DataLoadDebounceDelay + 350);

    private static RangeSplineObject SeedRangeSpline() => new RangeSplineObject(
        new ChartPoint(DateTime.Now.Date.AddDays(-30), FixedPriceDataService.FixedPrice),
        new ChartPoint(DateTime.Now.Date.AddDays(-10), FixedPriceDataService.FixedPrice));

    [Fact]
    public async Task RestoredRangeSpline_IsRecalculated_SoItIsRenderedAndSelectableWithoutBeingTouched()
    {
        // Pre-seed a file as if it was saved in a previous session before an app restart.
        // Deliberately NOT calling Recalculate() before saving, matching what actually
        // happens on a real save (only Points[] and other public settable properties are
        // persisted; _extractedPoints is never serialized).
        var repo = new ChartDrawingRepository();
        var seeded = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject> { SeedRangeSpline() }
        };
        await repo.SaveAsync(TickerRestore, TimeframeType.Daily, seeded);

        _sut.Symbol = TickerRestore;
        await SettleAsync();

        Assert.Single(_sut.ObjectManager.Objects);
        var restored = Assert.IsType<RangeSplineObject>(_sut.ObjectManager.Objects[0]);

        Assert.True(restored.ExtractedPoints.Count >= 2,
            "Expected the restored RangeSpline to have been recalculated (ExtractedPoints populated) without the user needing to drag it first.");

        // HitTest must succeed at a point actually on the extracted curve, proving the
        // object is selectable (and therefore draggable/deletable via the normal
        // click-to-select flow) immediately after restore.
        var transform = new LinearCoordinateTransform(
            DateTime.Now.AddDays(-35), DateTime.Now.AddDays(-5), 0m, 200m, 1000, 600);
        var midPoint = restored.ExtractedPoints[restored.ExtractedPoints.Count / 2];
        var midScreen = transform.ChartToScreen(midPoint);

        Assert.True(restored.HitTest(new global::Avalonia.Point(midScreen.X, midScreen.Y), transform),
            "Expected the restored RangeSpline to be hit-testable (selectable) immediately after restore, without needing to be dragged first.");
    }

    /// <summary>
    /// Simulates the actual two-click user gesture (click once to select, release, click
    /// again on the handle to start a handle-specific drag) on a restored RangeSpline,
    /// using a deterministic candle price so Points[] genuinely coincides with the real
    /// rendered curve (matching realistic usage, where the user's original click landed
    /// on/near the visible price data).
    /// </summary>
    [Fact]
    public async Task RestoredRangeSpline_TwoClickSelectThenHandleDrag_ViaRealController()
    {
        var repo = new ChartDrawingRepository();
        var seeded = new Dictionary<ChartDrawingContextType, List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new List<IChartObject> { SeedRangeSpline() }
        };
        await repo.SaveAsync(TickerRestore, TimeframeType.Daily, seeded);

        _sut.Symbol = TickerRestore;
        await SettleAsync();
        _sut.CurrentTool = StockAnalyzer.Avalonia.Drawing.DrawingTool.Pointer;

        var restored = Assert.IsType<RangeSplineObject>(_sut.ObjectManager.Objects[0]);
        var transform = new LinearCoordinateTransform(
            DateTime.Now.AddDays(-35), DateTime.Now.AddDays(-5), 0m, 200m, 1000, 600);
        var controller = new ChartInteractionController(new MagnetSnapService(), new DialogService(), new SmartGuideService());

        // Click 1: on Points[0] (also the curve's start, since the seeded price matches
        // the real candle price), nothing selected yet -> selects the object.
        var handleScreen = transform.ChartToScreen(restored.Points[0]);
        var clickPoint = new global::Avalonia.Point(handleScreen.X, handleScreen.Y);
        bool firstPressed = controller.HandlePointerPressed(clickPoint, clickPoint, _sut, transform, KeyModifiers.None, 1);
        Assert.True(firstPressed, "Expected the first click on the restored RangeSpline to select it.");
        Assert.True(restored.IsSelected, "Expected the restored RangeSpline to become selected after the first click.");
        controller.HandlePointerReleased(_sut);

        // Click 2: on the same Points[0] handle, now that it IS selected -> should start a
        // handle-specific drag this time.
        bool secondPressed = controller.HandlePointerPressed(clickPoint, clickPoint, _sut, transform, KeyModifiers.None, 1);
        Assert.True(secondPressed, "Expected the second click on the handle to register a press.");
        Assert.Same(restored, controller.DraggedObject);
        Assert.Equal(0, controller.DraggedHandleIndex);

        // Actually drag it and confirm Points[0] updates.
        var snapshot = new ChartDataSnapshot(_sut.Candles);
        var bounds = new global::Avalonia.Rect(0, 0, 1000, 600);
        var farPoint = new ChartPoint(DateTime.Now.Date.AddDays(-32), FixedPriceDataService.FixedPrice);
        var movePoint = transform.ChartToScreen(farPoint);
        controller.HandlePointerMoved(movePoint, movePoint, _sut, transform, snapshot, bounds, 0, 0, out _, KeyModifiers.None);

        Assert.Equal(farPoint.Time, restored.Points[0].Time);
    }

    /// <summary>
    /// Guards against the mismatch that made the deterministic tests above necessary in
    /// the first place: if the user's original click price was NOT exactly on a real
    /// candle (e.g. it landed slightly above/below the visible price action, or on totally
    /// unrelated random test data), RangeSplineObject.Recalculate() must correct Points[]
    /// to the extracted curve's real start/end price so the drawn handle always coincides
    /// with where the curve is actually rendered -- otherwise clicking the visible curve's
    /// endpoint would never land on the (offset) handle used to start a drag.
    /// </summary>
    [Fact]
    public void Recalculate_CorrectsMismatchedPointsPriceToMatchExtractedCurve()
    {
        var candles = new List<CoreCandleData>();
        var now = DateTime.Now.Date;
        for (int i = 0; i < 60; i++)
        {
            candles.Add(new CoreCandleData(now.AddDays(-60 + i), 9000m, 9010m, 8990m, 9000m, 1000));
        }

        // Deliberately far-off price (100), simulating a click nowhere near the real data.
        var spline = new RangeSplineObject(
            new ChartPoint(now.AddDays(-30), 100m),
            new ChartPoint(now.AddDays(-10), 100m));

        spline.Recalculate(candles);

        Assert.True(spline.ExtractedPoints.Count >= 2);
        Assert.Equal(spline.ExtractedPoints[0].Price, spline.Points[0].Price);
        Assert.Equal(spline.ExtractedPoints[^1].Price, spline.Points[1].Price);
        Assert.NotEqual(100m, spline.Points[0].Price);
    }
}
