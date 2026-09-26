using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// Wiring of the drawing objects list: the pointer entering the panel must bring a stale tab up to date
/// before any row command can target objects that another tab already replaced.
/// </summary>
public class DrawingObjectsViewRefreshTests : IDisposable
{
    private const string Ticker = "ZZTEST-LISTWIRE";
    private readonly ChartDrawingRepository _repository = new();
    private readonly DrawingDocumentSessionStore _sessionStore = new();
    private readonly ChartViewModel _tabA;
    private readonly ChartViewModel _tabB;

    public DrawingObjectsViewRefreshTests()
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
        foreach (var file in new[] { path, path + ChartDrawingRepository.ConflictFileSuffix, path + ".tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [AvaloniaFact]
    public async Task PointerEntered_RefreshesStaleTabAndRebuildsTheList()
    {
        _tabA.Symbol = Ticker;
        await StockAnalyzer.Avalonia.Tests.ViewModels.ChartViewModelTestWaits.AwaitLoadsAsync(_tabA);
        _tabB.Symbol = Ticker;
        await StockAnalyzer.Avalonia.Tests.ViewModels.ChartViewModelTestWaits.AwaitLoadsAsync(_tabB);

        var list = new DrawingObjectsViewModel(_tabB, new StockAnalyzer.Avalonia.Services.DispatcherService());
        var view = new DrawingObjectsView { DataContext = list };

        _tabA.ObjectManager.AddObject(new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m), new ChartPoint(new DateTime(2024, 1, 2), 12m)));
        _tabA.PersistCurrentDrawings();
        Assert.Empty(_tabB.ObjectManager.Objects);

        view.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, view, new global::Avalonia.Input.Pointer(0, PointerType.Mouse, true),
            view, new global::Avalonia.Point(1, 1), 0, new PointerPointProperties(), KeyModifiers.None) { Source = view });

        Assert.Single(_tabB.ObjectManager.Objects);
        Assert.Single(list.Items);
    }
}
