using Xunit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Views.Chart;
using System.Linq;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DataWindowViewModelTests
{
    private ChartViewModel _chartViewModel;
    private DataWindowViewModel _sut;

    public DataWindowViewModelTests()
    {
        // Use isolated messenger
        var messenger = new StrongReferenceMessenger();
        _chartViewModel = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!, // strategyFactory
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Avalonia.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
            null!, // marketStructureService
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new StockAnalyzer.Avalonia.Services.MockChartSettingsManager(),
            new SynchronousDispatcherService(),
            null,  // predictionService
            null!, // analysisPipelineService
            null,  // marketDataProvider
            null,  // pythonService
            null,  // comparisonDataAligner
            messenger: messenger
        );
        _sut = new DataWindowViewModel(_chartViewModel, messenger, new SynchronousDispatcherService());
    }

    [Fact]
    public void Receive_WithValidCandle_UpdatesProperties()
    {
        var candle = new CoreCandleData(new DateTime(2026, 1, 1, 10, 0, 0), 100m, 110m, 90m, 105m, 1000);
        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" });

        _sut.Receive(message);

        Assert.Equal("2026/01/01 10:00", _sut.DateText);
        Assert.Equal("100.000", _sut.OpenText);
        Assert.Equal("110.000", _sut.HighText);
        Assert.Equal("90.000", _sut.LowText);
        Assert.Equal("105.000", _sut.CloseText);
        Assert.Equal("1,000", _sut.VolumeText);
        Assert.Empty(_sut.IndicatorItems);
    }
    
    [Fact]
    public void Receive_WithValidCandleMidnight_ShowsOnlyDate()
    {
        var candle = new CoreCandleData(new DateTime(2026, 1, 1, 0, 0, 0), 100m, 110m, 90m, 105m, 1000);
        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" });

        _sut.Receive(message);

        Assert.Equal("2026/01/01", _sut.DateText);
    }

    [Fact]
    public void Receive_WithNullCandle_ClearsProperties()
    {
        _sut.DateText = "Old";
        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = -1, HoveredCandle = null, ChartSymbol = "TEST" });

        _sut.Receive(message);

        Assert.Empty(_sut.DateText);
        Assert.Empty(_sut.OpenText);
        Assert.Empty(_sut.IndicatorItems);
    }

    [Fact]
    public void Receive_WithRelativePerformance_ShowsSymbolPercentages()
    {
        // 1. Arrange RP Mode
        _chartViewModel.ChartType = ChartType.RelativePerformance;
        _chartViewModel.VisibleStartIndex = 0;
        
        var primaryCandles = new[] { 
            new CoreCandleData(new DateTime(2026, 1, 1), 100m, 100m, 100m, 100m, 1000), // Base
            new CoreCandleData(new DateTime(2026, 1, 2), 100m, 100m, 100m, 105m, 1000)  // +5%
        };
        
        var comparisonSeries = new CandleData?[] {
            new CandleData(new DateTime(2026, 1, 1), 50m, 50m, 50m, 50m, 500),   // Base
            new CandleData(new DateTime(2026, 1, 2), 50m, 50m, 50m, 49m, 500)    // -2%
        };
        
        var series = new Dictionary<string, CandleData?[]> {
            { "COMP", comparisonSeries }
        };
        
        // Mock comparison data in ChartViewModel
        series["AAPL"] = primaryCandles.Select(c => (CandleData?)new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToArray();
        _chartViewModel.Symbol = "AAPL";
        _chartViewModel.ChartType = ChartType.RelativePerformance;
        _chartViewModel.ComparisonMode = ComparisonMode.Performance;
        

        // CRITICAL: Set CurrentSnapshot for DataWindowViewModel to proceed
        var compBase = comparisonSeries[0]?.Close ?? 1m;
        var aaplBase = primaryCandles[0].Close;
        _chartViewModel.CurrentSnapshot = new StockAnalyzer.Avalonia.Views.Chart.ChartDataSnapshot(
            coreCandles: primaryCandles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            symbol: "AAPL",
            comparisonSeries: new Dictionary<string, decimal?[]> {
                { "COMP", comparisonSeries.Select(c => (c?.Close - compBase) / compBase * 100m).ToArray() },
                { "AAPL", primaryCandles.Select(c => (decimal?)((c.Close - aaplBase) / aaplBase * 100m)).ToArray() }
            },
            chartType: ChartType.RelativePerformance
        );

        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { 
            CandleIndex = 1, 
            HoveredCandle = primaryCandles[1], 
            ChartSymbol = "AAPL" 
        });

        // 2. Act
        _sut.Receive(message);

        // 3. Assert
        Assert.False(_sut.IsOpenVisible);
        Assert.False(_sut.IsHighVisible);
        
        // Items should be COMP and AAPL
        Assert.Equal(2, _sut.IndicatorItems.Count);
        
        var compItem = _sut.IndicatorItems.FirstOrDefault(i => i.Name == "COMP");
        Assert.NotNull(compItem);
        Assert.Equal("-2.000%", compItem.Value);
        // Correct color check
        Assert.NotEqual(IndicatorColor.Transparent, compItem.Color);

        var primaryItem = _sut.IndicatorItems.FirstOrDefault(i => i.Name == "AAPL");
        Assert.NotNull(primaryItem);
        Assert.Equal("+5.000%", primaryItem.Value);
    }

    [Fact]
    public void Symbol_ReturnsFallback_WhenChartSymbolIsEmptyOrNull()
    {
        _chartViewModel.Symbol = string.Empty;
        Assert.Equal("-", _sut.Symbol);
    }

    [Fact]
    public void Symbol_Updates_WhenChartSymbolChanges()
    {
        bool eventFired = false;
        _sut.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DataWindowViewModel.Symbol))
            {
                eventFired = true;
            }
        };

        _chartViewModel.Symbol = "MSFT";

        Assert.Equal("MSFT", _sut.Symbol);
        Assert.True(eventFired);
    }

    [Fact]
    public void UpdateIndicatorItems_WithFFTCycle_ShowsAllThreeSeriesWithCorrectDisplayNamesAndValues()
    {
        // 1. Arrange FFT Cycle
        var fftCycle = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.FFTCycle);
        fftCycle.IsEnabled = true;

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(new DateTime(2026, 1, 1), 100m, 110m, 90m, 105m, 1000)
        };
        _chartViewModel.Candles = candles;

        var resultDict = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { "Main", new decimal?[] { 24.0m } },
            { "CycleStrength", new decimal?[] { 3.5m } },
            { "Oscillator", new decimal?[] { 0.45m } }
        };
        var indicatorResult = StockAnalyzer.Core.Models.Indicators.IndicatorResult.Success(resultDict);

        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(fftCycle);
        _chartViewModel.IndicatorResults = new Dictionary<string, StockAnalyzer.Core.Models.Indicators.IIndicatorResult>
        {
            { fftCycle.Id, indicatorResult }
        };

        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData
        {
            CandleIndex = 0,
            HoveredCandle = candles[0],
            ChartSymbol = "TEST"
        });

        // 2. Act
        _sut.Receive(message);

        // 3. Assert
        Assert.Single(_sut.IndicatorItems);
        var parentItem = _sut.IndicatorItems[0];
        Assert.True(parentItem.HasChildren);
        Assert.Equal(3, parentItem.Children.Count);

        var periodChild = parentItem.Children[0];
        Assert.Equal("FFT Cycle", periodChild.Name);
        Assert.Equal("24.000", periodChild.Value);

        var strengthChild = parentItem.Children[1];
        Assert.Equal("FFT Cycle Strength", strengthChild.Name);
        Assert.Equal("3.500", strengthChild.Value);

        var oscChild = parentItem.Children[2];
        Assert.Equal("FFT Cycle Oscillator", oscChild.Name);
        Assert.Equal("0.450", oscChild.Value);
    }
}
