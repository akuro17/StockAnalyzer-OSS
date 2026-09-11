using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Reproduces (with the real ChartViewModel + real ChartDrawingRepository) the "switch symbol
/// away and back loses drawings" bug reported after Task 2, to get objective evidence of the
/// root cause per sa_minimal_fix's Step 1 ("prefer executable reproduction... do not attempt a
/// second-guess fix without evidence").
/// </summary>
public class ChartViewModelDrawingPersistenceTests : IDisposable
{
    private const string TickerA = "ZZTEST-DRAWA";
    private const string TickerB = "ZZTEST-DRAWB";
    private const string TickerC = "ZZTEST-DISP";
    private const string TickerD = "ZZTEST-DRAWD";
    private readonly ChartDrawingRepository _sharedRepository = new();
    private readonly ChartViewModel _sut;
    private bool _sutDisposedByTest;

    public ChartViewModelDrawingPersistenceTests()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });

        _sut = CreateChartViewModel(settingsManager, new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
    }

    private ChartViewModel CreateChartViewModel(
        StockAnalyzer.Avalonia.Services.MockChartSettingsManager settingsManager,
        CommunityToolkit.Mvvm.Messaging.IMessenger messenger)
    {
        return new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!, // strategyFactory
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Avalonia.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!, // marketStructureService
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,  // predictionService
            null!, // analysisPipelineService
            null,  // marketDataProvider
            null,  // pythonService
            null,  // comparisonDataAligner
            messenger: messenger,
            drawingRepository: _sharedRepository
        );
    }

    public void Dispose()
    {
        if (!_sutDisposedByTest) _sut.Dispose();
        foreach (var ticker in new[] { TickerA, TickerB, TickerC, TickerD })
        {
            var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
            var path = Path.Combine(dir, $"{ticker}.{TimeframeType.Daily}.json");
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Task SettleAsync() => Task.Delay(ChartConstants.DataLoadDebounceDelay + 350);

    [Fact]
    public async Task SwitchingSymbolAwayAndBack_PreservesDrawing()
    {
        _sut.Symbol = TickerA;
        await SettleAsync();

        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m));
        _sut.ObjectManager.AddObject(trendLine);
        await SettleAsync(); // let the fire-and-forget PersistCurrentDrawings() finish

        _sut.Symbol = TickerB;
        await SettleAsync();
        Assert.Empty(_sut.ObjectManager.Objects);

        _sut.Symbol = TickerA;
        await SettleAsync();

        Assert.Single(_sut.ObjectManager.Objects);
    }

    /// <summary>
    /// MainWindowViewModel.SelectedTicker's setter sets ChartViewModel.Symbol (which itself
    /// triggers OnSymbolChanged -> LoadDataCommand.ExecuteAsync) AND THEN explicitly calls
    /// ChartViewModel.LoadDataCommand.ExecuteAsync(null) again on the next line. This test
    /// mimics that double-fire (rather than only setting Symbol, as the test above does) to
    /// check whether the redundant second command invocation is what causes the reported
    /// data-loss bug.
    /// </summary>
    [Fact]
    public async Task SwitchingSymbolAwayAndBack_ViaDoubleFirePattern_PreservesDrawing()
    {
        _sut.Symbol = TickerA;
        _ = _sut.LoadDataCommand.ExecuteAsync(null);
        await SettleAsync();

        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m));
        _sut.ObjectManager.AddObject(trendLine);
        await SettleAsync();

        _sut.Symbol = TickerB;
        _ = _sut.LoadDataCommand.ExecuteAsync(null);
        await SettleAsync();
        Assert.Empty(_sut.ObjectManager.Objects);

        _sut.Symbol = TickerA;
        _ = _sut.LoadDataCommand.ExecuteAsync(null);
        await SettleAsync();

        Assert.Single(_sut.ObjectManager.Objects);
    }

    /// <summary>
    /// Reproduces the second reported bug: a drawing restored from a pre-existing file (as if
    /// loaded right after an app restart), deleted via Shift+Click (ObjectManager.RemoveObject,
    /// which fires the fire-and-forget PersistCurrentDrawings save), then the user immediately
    /// (no settle delay, unlike the tests above) switches symbol away and back. If the delete's
    /// fire-and-forget write races the switch-away's write, the old (pre-delete) content could
    /// resurface.
    /// </summary>
    [Fact]
    public async Task DeleteThenImmediatelySwitchSymbolAwayAndBack_KeepsDrawingDeleted()
    {
        // Pre-seed a file, as if it was saved in a previous session before this restart.
        var repo = new ChartDrawingRepository();
        var seeded = new System.Collections.Generic.Dictionary<ChartDrawingContextType, System.Collections.Generic.List<IChartObject>>
        {
            [ChartDrawingContextType.Standard] = new System.Collections.Generic.List<IChartObject>
            {
                new TrendLineObject(new ChartPoint(new DateTime(2024, 1, 1), 10m), new ChartPoint(new DateTime(2024, 1, 2), 12m))
            }
        };
        await repo.SaveAsync(TickerA, TimeframeType.Daily, seeded);

        _sut.Symbol = TickerA;
        await SettleAsync();
        Assert.Single(_sut.ObjectManager.Objects); // restored from the seeded file

        var toDelete = _sut.ObjectManager.Objects[0];
        _sut.ObjectManager.RemoveObject(toDelete.Id); // Shift+Click delete path
        // No settle here: switch immediately, before the fire-and-forget save necessarily lands.

        _sut.Symbol = TickerB;
        await SettleAsync();
        Assert.Empty(_sut.ObjectManager.Objects);

        _sut.Symbol = TickerA;
        await SettleAsync();

        Assert.Empty(_sut.ObjectManager.Objects); // must stay deleted, not resurrect the seeded drawing
    }

    /// <summary>
    /// Task 3: Dispose() (called when the app shuts down, via the DI container disposing every
    /// resolved ChartViewModel) must synchronously flush any not-yet-persisted drawing to disk,
    /// since PersistCurrentDrawings' fire-and-forget Save() could otherwise be abandoned mid-write
    /// if the process exits before its background Task.Run completes.
    /// </summary>
    [Fact]
    public async Task Dispose_FlushesPendingDrawingSynchronously_WithoutWaitingForFireAndForgetSave()
    {
        _sut.Symbol = TickerC;
        await SettleAsync();

        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m));
        _sut.ObjectManager.AddObject(trendLine);
        // No settle: Dispose() must flush this synchronously, not rely on the fire-and-forget save.

        _sut.Dispose();
        _sutDisposedByTest = true;

        var repo = new ChartDrawingRepository();
        var restored = repo.Load(TickerC, TimeframeType.Daily);
        Assert.NotNull(restored);
        Assert.Single(restored![ChartDrawingContextType.Standard]);

        // Let AddObject's own fire-and-forget auto-save (redundant with, but racing, the
        // explicit Dispose() flush above) settle before the fixture deletes TickerC's file,
        // so it can't resurrect the file after cleanup and leak into a later test run.
        await SettleAsync();
    }

    /// <summary>
    /// Bug (reported after Task 3): drawing on a ticker and restarting without switching symbol
    /// first loses the drawing, but switching once first preserves it. Root cause: the shutdown
    /// flush only ran from ChartViewModel.Dispose(), which depends on the DI container's
    /// cascading disposal during desktop.ShutdownRequested - not guaranteed to run/complete before
    /// the process exits. MainWindowViewModel.ForceSaveOnShutdown() (which runs deterministically
    /// during Window.OnClosing, the same proven-reliable path already used for workspace/
    /// DetachedTabs persistence) now calls this method directly, without disposing the
    /// ChartViewModel. This test proves FlushPendingDrawings() persists synchronously on its own
    /// and leaves the ChartViewModel fully usable afterward (Window.OnClosing can be cancelled by
    /// other logic, so the view model must not be torn down as a side effect of the flush).
    /// </summary>
    [Fact]
    public async Task FlushPendingDrawings_PersistsWithoutDisposing_AndViewModelRemainsUsable()
    {
        _sut.Symbol = TickerC;
        await SettleAsync();

        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 12m));
        _sut.ObjectManager.AddObject(trendLine);
        // No settle: FlushPendingDrawings() must persist this synchronously on its own, exactly
        // as MainWindowViewModel.ForceSaveOnShutdown() calls it - not by falling through to
        // Dispose(), which is not invoked here.

        _sut.FlushPendingDrawings();

        var repo = new ChartDrawingRepository();
        var restored = repo.Load(TickerC, TimeframeType.Daily);
        Assert.NotNull(restored);
        Assert.Single(restored![ChartDrawingContextType.Standard]);

        // The view model must remain fully usable after the flush (Window.OnClosing can still be
        // cancelled elsewhere), unlike Dispose() which tears it down: switching symbol still
        // works and correctly loads/persists via the normal path.
        _sut.Symbol = TickerA;
        await SettleAsync();
        Assert.Empty(_sut.ObjectManager.Objects);

        await SettleAsync();
    }

    /// <summary>
    /// Reported (2026-08-11): "Angle Tool isn't saved" - then, on further testing, "several other
    /// tools aren't restored either, while others are" (i.e. not tool-specific). Real-app
    /// diagnostic logging (Y:\Temp\sa_angle_debug.log) showed the actual cause: a second
    /// ChartViewModel instance (e.g. a synced panel chart - IsSyncEnabled defaults to true, so a
    /// ticker selection is broadcast to and independently reloaded by every synced chart) can load
    /// the same ticker+timeframe before the instance holding the real drawing has saved it,
    /// getting an empty read back. That second instance's own "save the previous owner's state
    /// before switching away" step (LoadDataInternalAsync) then persists its empty snapshot,
    /// clobbering the first instance's real content - observed as a rapid alternating sequence of
    /// populated/empty saves to the same file, empty landing last. This test reproduces exactly
    /// that with two real ChartViewModel instances sharing one ChartDrawingRepository.
    /// </summary>
    [Fact]
    public async Task SecondChartViewModelInstance_RacingSameTickerWithoutEverSeeingRealContent_DoesNotClobberFirstInstancesDrawing()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });
        var panel = CreateChartViewModel(settingsManager, new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
        try
        {
            // Both instances load TickerD before anything has ever been drawn on it (matches the
            // race: the panel's own Load never observes real content for this key).
            _sut.Symbol = TickerD;
            await SettleAsync();
            panel.Symbol = TickerD;
            await SettleAsync();

            // Draw on the "main" instance only.
            var trendLine = new TrendLineObject(
                new ChartPoint(new DateTime(2024, 1, 1), 10m),
                new ChartPoint(new DateTime(2024, 1, 2), 12m));
            _sut.ObjectManager.AddObject(trendLine);
            await SettleAsync(); // let the main instance's fire-and-forget save land on disk

            // The panel instance redundantly re-runs its load for the same (unchanged) symbol -
            // e.g. a spurious re-sync ping - which still triggers LoadDataInternalAsync's
            // "save previous owner" step using the panel's own (still empty) ObjectManager.
            _ = panel.LoadDataCommand.ExecuteAsync(null);
            await SettleAsync();

            // A fresh instance loading TickerD (simulating an app restart) must still see the
            // drawing from the main instance, not the panel's clobbering empty save.
            var afterRestart = CreateChartViewModel(settingsManager, new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger());
            try
            {
                afterRestart.Symbol = TickerD;
                await SettleAsync();
                Assert.Single(afterRestart.ObjectManager.Objects);
            }
            finally
            {
                afterRestart.Dispose();
            }
        }
        finally
        {
            panel.Dispose();
        }
    }
}
