using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DataWindowDrawingIntegrationTests
{
    private readonly ChartViewModel _chartViewModel;
    private readonly DataWindowViewModel _sut;
    private readonly IMessenger _messenger;

    public DataWindowDrawingIntegrationTests()
    {
        _messenger = new StrongReferenceMessenger();
        _chartViewModel = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!,
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Avalonia.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            null,
            messenger: _messenger
        );

        _sut = new DataWindowViewModel(_chartViewModel, _messenger, new SynchronousDispatcherService());
    }

    [Fact]
    public void DataWindowViewModel_PopulatesDrawingItems_WhenDrawingObjectExists()
    {
        var dt = new DateTime(2026, 1, 1);
        var entry = new ChartPoint(dt, 100m);
        var stop = new ChartPoint(dt, 90m);
        var target = new ChartPoint(dt, 120m);
        var longShort = new LongShortPositionObject(entry, stop, target, isLong: true);

        _chartViewModel.ObjectManager.AddObject(longShort);

        var candle = new CoreCandleData(dt, 100m, 110m, 90m, 100m, 1000);
        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" });

        _sut.Receive(message);

        Assert.True(_sut.HasItems);
        Assert.Single(_sut.DrawingItems);

        var groupItem = _sut.DrawingItems[0];
        Assert.True(groupItem.HasChildren);
        Assert.Equal(4, groupItem.Children.Count);
        Assert.Contains(groupItem.Children, c => c.Name == "Entry" && c.Value == "100.00");
        Assert.Contains(groupItem.Children, c => c.Name == "R/R Ratio" && c.Value == "1 : 2.00");
    }

    [Fact]
    public void DataWindowViewModel_ExcludesHiddenDrawingObjects()
    {
        var dt = new DateTime(2026, 1, 1);
        var line = new HorizontalLineObject(new ChartPoint(dt, 125m));
        _chartViewModel.ObjectManager.AddObject(line);

        var candle = new CoreCandleData(dt, 100m, 110m, 90m, 100m, 1000);
        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" }));

        Assert.Single(_sut.DrawingItems);

        // Hide object
        _chartViewModel.ObjectManager.ToggleVisibility(line.Id);
        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" }));

        Assert.Empty(_sut.DrawingItems);
    }

    [Fact]
    public void DataWindowViewModel_SyncsOnObjectManagerChanges()
    {
        var dt = new DateTime(2026, 1, 1);
        var line = new HorizontalLineObject(new ChartPoint(dt, 200m));
        _chartViewModel.ObjectManager.AddObject(line);

        // ObjectManager.AddObject triggers Synced event which invokes UpdatePropertiesInternal via Dispatcher
        Assert.Single(_sut.DrawingItems);
        Assert.Contains(_sut.DrawingItems, it => it.Value == "200.000");

        // Remove object
        _chartViewModel.ObjectManager.RemoveObject(line.Id);
        Assert.Empty(_sut.DrawingItems);
    }
}
