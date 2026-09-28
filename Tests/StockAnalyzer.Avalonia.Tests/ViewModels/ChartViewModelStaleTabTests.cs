using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Two ChartViewModels on one shared repository and one ticker+timeframe: a stale tab must pick up what the
/// other tab persisted before it edits (A), and must never overwrite it silently (D).
/// </summary>
public class ChartViewModelStaleTabTests : IDisposable
{
    private const string Ticker = "ZZTEST-STALETAB";
    private const string OtherTicker = "ZZTEST-STALEOTH";
    private readonly ChartDrawingRepository _repository = new();
    private readonly DrawingDocumentSessionStore _sessionStore = new();
    private readonly ChartViewModel _tabA;
    private readonly ChartViewModel _tabB;

    public ChartViewModelStaleTabTests()
    {
        _tabA = CreateTab();
        _tabB = CreateTab();
    }

    private ChartViewModel CreateTab()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });
        return new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!,
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Avalonia.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            settingsManager,
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            null,
            messenger: new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger(),
            drawingRepository: _repository,
            sessionStore: _sessionStore);
    }

    public void Dispose()
    {
        _tabA.Dispose();
        _tabB.Dispose();
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        var path = Path.Combine(dir, $"{Ticker}.{TimeframeType.Daily}.json");
        foreach (var file in new[] { path, path + ChartDrawingRepository.ConflictFileSuffix, path + ".tmp", path + ChartDrawingRepository.ConflictFileSuffix + ".tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static TrendLineObject Line(decimal price) =>
        new(new ChartPoint(new DateTime(2024, 1, 1), price), new ChartPoint(new DateTime(2024, 1, 2), price + 2m));

    private async Task OpenBothTabsAsync()
    {
        _tabA.Symbol = Ticker;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_tabA);
        _tabB.Symbol = Ticker;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_tabB);
    }

    private int PersistedCount() =>
        _repository.LoadPayload(Ticker, TimeframeType.Daily)?.Objects.GetValueOrDefault(ChartDrawingContextType.Standard)?.Count ?? 0;

    [Fact]
    public async Task StaleTab_IsRefreshedBeforeItsEdit_SoBothTabsObjectsSurvive()
    {
        await OpenBothTabsAsync();

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();
        Assert.Empty(_tabB.ObjectManager.Objects); // no live sync: B still shows the old state

        var result = _tabB.DrawingEditCoordinator!.ExecuteEdit(DrawingOperationKind.Add, () => _tabB.ObjectManager.AddObject(Line(20m)));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _tabB.ObjectManager.Objects.Count); // A's object was loaded before B's edit
        Assert.Equal(2, PersistedCount());
        Assert.False(File.Exists(ConflictPath()));
    }

    [Fact]
    public async Task EditThroughPathThatBypassesRefresh_IsRejectedNotOverwriting_AndKeepsConflictBackup()
    {
        await OpenBothTabsAsync();

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();

        _tabB.ObjectManager.AddObject(Line(20m)); // direct mutation, no coordinator: no refresh first
        _tabB.PersistCurrentDrawings();

        Assert.Equal(1, PersistedCount());                 // A's file untouched
        Assert.Single(_tabB.ObjectManager.Objects);        // B adopted the file
        Assert.True(File.Exists(ConflictPath()));          // B's rejected state is recoverable
    }

    [Fact]
    public async Task StaleTab_SwitchingAway_DoesNotOverwriteNewerFileNorCreateConflict()
    {
        await OpenBothTabsAsync();

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();

        _tabB.Symbol = OtherTicker;
        await ChartViewModelTestWaits.AwaitLoadsAsync(_tabB);

        Assert.Equal(1, PersistedCount());
        Assert.False(File.Exists(ConflictPath()));
    }

    [Fact]
    public async Task UpToDateTab_IsNotRefreshed()
    {
        await OpenBothTabsAsync();

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();

        Assert.False(_tabA.RefreshDrawingsIfStale());
        Assert.True(_tabB.RefreshDrawingsIfStale());
        Assert.Single(_tabB.ObjectManager.Objects);
        Assert.False(_tabB.RefreshDrawingsIfStale());
    }

    [Fact]
    public async Task Refresh_IsSkippedWhileATransactionIsOpen()
    {
        await OpenBothTabsAsync();

        var begin = _tabB.DrawingEditCoordinator!.BeginEdit(DrawingOperationKind.Add);
        Assert.True(begin.IsSuccess);

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();

        Assert.False(_tabB.RefreshDrawingsIfStale()); // objects must not be replaced under the open transaction

        _tabB.DrawingEditCoordinator.Cancel(begin.Token);
        Assert.True(_tabB.RefreshDrawingsIfStale());
    }

    [Fact]
    public async Task DrawingsList_RefreshesFromRepository_BeforeRowCommandsCanTargetStaleObjects()
    {
        await OpenBothTabsAsync();
        // Synchronous dispatcher: this is a plain [Fact] on an xUnit thread, and the real dispatcher would post the list's re-sync to the Avalonia UI
        // thread, where it can run concurrently with the direct RefreshFromRepositoryIfStale() call (their _isSyncing guard is not atomic), so the
        // assertion below could observe a half-built list.
        var list = new DrawingObjectsViewModel(_tabB, new SynchronousDispatcherService());

        _tabA.ObjectManager.AddObject(Line(10m));
        _tabA.PersistCurrentDrawings();
        Assert.Empty(_tabB.ObjectManager.Objects);

        list.RefreshFromRepositoryIfStale();

        Assert.Single(_tabB.ObjectManager.Objects);
        Assert.Single(list.Items);
    }

    private static string ConflictPath()
    {
        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        return Path.Combine(dir, $"{Ticker}.{TimeframeType.Daily}.json" + ChartDrawingRepository.ConflictFileSuffix);
    }
}
