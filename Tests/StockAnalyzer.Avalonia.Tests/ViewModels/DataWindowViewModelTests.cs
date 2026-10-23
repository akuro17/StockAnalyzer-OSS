using StockAnalyzer.Core.Services;
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
using System.Globalization;
using System.Linq;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// The rows are formatted with the invariant culture whatever the culture of the machine is, so every test runs under a culture with a different decimal and group separator (de-DE).
public class DataWindowViewModelTests : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private ChartViewModel _chartViewModel;
    private DataWindowViewModel _sut;
    private readonly IMessenger _messenger;

    public DataWindowViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        // Use isolated messenger
        var messenger = new StrongReferenceMessenger();
        _messenger = messenger;
        _chartViewModel = new ChartViewModel(
            new StockAnalyzer.Avalonia.Services.MockDataService(),
            new StockAnalyzer.Avalonia.Services.DialogService(),
            null!, // strategyFactory
            new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings(),
            new StockAnalyzer.Core.Services.TimeFrameManager(new StockAnalyzer.Avalonia.Services.MockDataService()),
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

    public void Dispose() => CultureInfo.CurrentCulture = _previousCulture;

    [Fact]
    public void Receive_WithValidCandle_UpdatesProperties()
    {
        var candle = new CoreCandleData(new DateTime(2026, 1, 1, 10, 0, 0), 100m, 110m, 90m, 105m, 1000);
        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, HoveredCandle = candle, ChartSymbol = "TEST" });

        _sut.Receive(message);

        Assert.Equal("2026-01-01 10:00", _sut.DateText);
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

        Assert.Equal("2026-01-01", _sut.DateText);
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

    [Fact]
    public void NavigatePreviousCategory_CyclesCorrectly()
    {
        // Initial state: MarketData
        Assert.Equal(DataWindowCategory.MarketData, _sut.SelectedCategory!.Category);
        Assert.True(_sut.IsMarketDataSelected);
        Assert.False(_sut.IsIndicatorsSelected);
        Assert.False(_sut.IsDrawingToolsSelected);

        // Previous -> DrawingTools (rotate left wrap-around)
        _sut.NavigatePreviousCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.DrawingTools, _sut.SelectedCategory!.Category);
        Assert.False(_sut.IsMarketDataSelected);
        Assert.False(_sut.IsIndicatorsSelected);
        Assert.True(_sut.IsDrawingToolsSelected);

        // Previous -> TechnicalIndicators
        _sut.NavigatePreviousCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.TechnicalIndicators, _sut.SelectedCategory!.Category);
        Assert.False(_sut.IsMarketDataSelected);
        Assert.True(_sut.IsIndicatorsSelected);
        Assert.False(_sut.IsDrawingToolsSelected);

        // Previous -> MarketData
        _sut.NavigatePreviousCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.MarketData, _sut.SelectedCategory!.Category);
        Assert.True(_sut.IsMarketDataSelected);
    }

    [Fact]
    public void NavigateNextCategory_CyclesCorrectly()
    {
        // Initial state: MarketData
        Assert.Equal(DataWindowCategory.MarketData, _sut.SelectedCategory!.Category);

        // Next -> TechnicalIndicators
        _sut.NavigateNextCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.TechnicalIndicators, _sut.SelectedCategory!.Category);
        Assert.True(_sut.IsIndicatorsSelected);

        // Next -> DrawingTools
        _sut.NavigateNextCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.DrawingTools, _sut.SelectedCategory!.Category);
        Assert.True(_sut.IsDrawingToolsSelected);

        // Next -> MarketData (wrap-around)
        _sut.NavigateNextCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.MarketData, _sut.SelectedCategory!.Category);
        Assert.True(_sut.IsMarketDataSelected);
    }

    [Fact]
    public void UpdateIndicatorItems_OrdersByMainWindowSubWindowPanelAndName()
    {
        var candles = new[] { new CoreCandleData(new DateTime(2026, 1, 1), 100m, 110m, 90m, 105m, 1000) };
        _chartViewModel.Candles = candles;

        // Create mock indicators across different windows and panels:
        // 1. Sub-window, Panel B: Stochastic
        var indSubB = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "Stochastic",
            IsOverlay = false,
            OverlayPanelId = "B",
            IsEnabled = true
        };
        // 2. Main-window: SMA
        var indMainSma = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "SMA",
            IsOverlay = true,
            IsEnabled = true
        };
        // 3. Sub-window, Default panel: Volume
        var indSubDefVol = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "Volume",
            IsOverlay = false,
            OverlayPanelId = null,
            IsEnabled = true
        };
        // 4. Main-window: Bollinger Bands
        var indMainBb = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "Bollinger Bands",
            IsOverlay = true,
            IsEnabled = true
        };
        // 5. Sub-window, Panel A: RSI
        var indSubARsi = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "RSI",
            IsOverlay = false,
            OverlayPanelId = "A",
            IsEnabled = true
        };
        // 6. Sub-window, Default panel: MACD
        var indSubDefMacd = new CoreIndicatorSettings
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "MACD",
            IsOverlay = false,
            OverlayPanelId = "",
            IsEnabled = true
        };

        // Add them in arbitrary order
        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(indSubB);
        _chartViewModel.Indicators.Add(indMainSma);
        _chartViewModel.Indicators.Add(indSubDefVol);
        _chartViewModel.Indicators.Add(indMainBb);
        _chartViewModel.Indicators.Add(indSubARsi);
        _chartViewModel.Indicators.Add(indSubDefMacd);

        var dummyResult = StockAnalyzer.Core.Models.Indicators.IndicatorResult.Success(
            new Dictionary<string, IReadOnlyList<decimal?>> { { "Main", new decimal?[] { 10.0m } } });

        _chartViewModel.IndicatorResults = new Dictionary<string, StockAnalyzer.Core.Models.Indicators.IIndicatorResult>
        {
            { indSubB.Id, dummyResult },
            { indMainSma.Id, dummyResult },
            { indSubDefVol.Id, dummyResult },
            { indMainBb.Id, dummyResult },
            { indSubARsi.Id, dummyResult },
            { indSubDefMacd.Id, dummyResult }
        };

        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData
        {
            CandleIndex = 0,
            HoveredCandle = candles[0],
            ChartSymbol = "TEST"
        });

        _sut.Receive(message);

        // Expected sorted order:
        // Main-window (ascending):
        // 0: "Bollinger Bands"
        // 1: "SMA"
        // Sub-window Default (ascending):
        // 2: "MACD"
        // 3: "Volume"
        // Sub-window Panel A:
        // 4: "RSI"
        // Sub-window Panel B:
        // 5: "Stochastic"
        Assert.Equal(6, _sut.IndicatorItems.Count);
        Assert.Equal("Bollinger Bands", _sut.IndicatorItems[0].Name);
        Assert.Equal("SMA", _sut.IndicatorItems[1].Name);
        Assert.Equal("MACD", _sut.IndicatorItems[2].Name);
        Assert.Equal("Volume", _sut.IndicatorItems[3].Name);
        Assert.Equal("RSI", _sut.IndicatorItems[4].Name);
        Assert.Equal("Stochastic", _sut.IndicatorItems[5].Name);
    }

    [Fact]
    public void SelectedCategory_WhenSetToNull_AutoRecoversToFirstCategory()
    {
        _sut.NavigateNextCategoryCommand.Execute(null);
        Assert.Equal(DataWindowCategory.TechnicalIndicators, _sut.SelectedCategory!.Category);

        // Assign null
        _sut.SelectedCategory = null;

        // Auto-recovers to first category (MarketData)
        Assert.NotNull(_sut.SelectedCategory);
        Assert.Equal(DataWindowCategory.MarketData, _sut.SelectedCategory.Category);
        Assert.True(_sut.IsMarketDataSelected);
    }

    [Fact]
    public void InitializeCategories_WithBracketedSentinel_FallsBackToEnglishDefaults()
    {
        var dummyService = new DummyMissingKeyLocalizationService();
        var vm = new DataWindowViewModel(_chartViewModel, _chartViewModel.Messenger, _chartViewModel.DispatcherService, dummyService);

        Assert.Equal(3, vm.AvailableCategories.Count);
        Assert.Equal("Market Data", vm.AvailableCategories[0].DisplayName);
        Assert.Equal("Technical Indicators", vm.AvailableCategories[1].DisplayName);
        Assert.Equal("Drawing Tools", vm.AvailableCategories[2].DisplayName);
    }

    [Fact]
    public void Receive_WithClothoidMovingAverage_DisplaysMAValueInIndicatorItems()
    {
        // Arrange
        var candle = new CoreCandleData(new DateTime(2026, 1, 1), 100m, 110m, 90m, 105m, 1000);

        var clothoidSetting = new CoreIndicatorSettings
        {
            Id = "clothoid_ma_test",
            TypeEnum = IndicatorType.ClothoidMovingAverage,
            DisplayName = "ClothoidMA (20, 0.00, 1.0)",
            IsOverlay = true,
            IsEnabled = true
        };

        var result = StockAnalyzer.Core.Models.Indicators.IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { "Main", new decimal?[] { 103.5m } },
            { "EffectiveLag", new decimal?[] { 2.45m } },
            { "EffectiveSampleSize", new decimal?[] { 5.12m } }
        });

        _chartViewModel.Indicators.Clear();
        _chartViewModel.Indicators.Add(clothoidSetting);
        _chartViewModel.IndicatorResults = new Dictionary<string, StockAnalyzer.Core.Models.Indicators.IIndicatorResult>
        {
            { clothoidSetting.Id, result }
        };

        var message = new CrosshairPositionChangedMessage(new CrosshairPositionData
        {
            CandleIndex = 0,
            HoveredCandle = candle,
            ChartSymbol = "TEST"
        });

        // Act
        _sut.Receive(message);

        // Assert: ClothoidMA group should exist and child item should show 103.500
        Assert.Single(_sut.IndicatorItems);
        var groupItem = _sut.IndicatorItems[0];
        Assert.Equal("ClothoidMovingAverage", groupItem.Name);
        Assert.Single(groupItem.Children);
        var childItem = groupItem.Children[0];
        Assert.Equal("103.500", childItem.Value);
    }

    private void ArrangePointAndFigureColumns(decimal boxSize)
    {
        // Three columns: 1 box (High == Low), 4 boxes (High - Low == 3 x box) and a column whose range rounds to 3 boxes (2.5 x box rounds to even => 2, +1).
        var times = new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), new DateTime(2026, 1, 3) };
        var highs = new[] { 100m * boxSize, 103m * boxSize, 102.5m * boxSize };
        var lows = new[] { 100m * boxSize, 100m * boxSize, 100m * boxSize };
        _chartViewModel.EffectivePnfBoxSize = boxSize;
        _chartViewModel.ChartType = ChartType.PointAndFigure;
        _chartViewModel.PnfDataAdapter = new StockAnalyzer.ZeroAllocation.ChartSegmentAdapter(
            StockAnalyzer.ZeroAllocation.ZeroAllocChartDataType.PointAndFigure,
            times, lows, highs, lows, highs, new long[3],
            new[]
            {
                new StockAnalyzer.ZeroAllocation.OriginalIndexRange(0, 1),
                new StockAnalyzer.ZeroAllocation.OriginalIndexRange(1, 1),
                new StockAnalyzer.ZeroAllocation.OriginalIndexRange(2, 1),
            });
        _chartViewModel.VisibleStartIndex = 0;
    }

    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "4")]
    [InlineData(2, "3")]
    public void Receive_WithPointAndFigure_ShowsTheColumnBoxCountInTheDataTab(int columnIndex, string expectedCount)
    {
        // The box count of the hovered column is shown in the Data tab only; the chart itself draws no count label.
        ArrangePointAndFigureColumns(boxSize: 2m);
        var candle = new CoreCandleData(new DateTime(2026, 1, 1).AddDays(columnIndex), 1m, 1m, 1m, 1m, 0);

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = columnIndex, HoveredCandle = candle, ChartSymbol = "TEST" }));

        Assert.Equal(expectedCount, _sut.PointAndFigureCountText);
    }

    [Fact]
    public void Receive_WithPointAndFigureAndNoPositiveBoxSize_LeavesTheCountEmpty()
    {
        ArrangePointAndFigureColumns(boxSize: 2m);
        _chartViewModel.EffectivePnfBoxSize = 0m;
        var candle = new CoreCandleData(new DateTime(2026, 1, 2), 1m, 1m, 1m, 1m, 0);

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 1, HoveredCandle = candle, ChartSymbol = "TEST" }));

        Assert.Equal(string.Empty, _sut.PointAndFigureCountText);
    }

    // ---- Non-time-series charts: the Data tab describes a column of the CONVERTED series (bricks, Kagi segments, P&F columns, Three Line Break blocks).
    // The converted series is shorter than the loaded candle list, and the hovered position is relative to the visible window.

    private static List<CoreCandleData> OriginalCandles(int count)
        => Enumerable.Range(0, count).Select(i => new CoreCandleData(new DateTime(2026, 1, 1).AddDays(i), 100m + i, 101m + i, 99m + i, 100.5m + i, 1000)).ToList();

    private static CrosshairPositionChangedMessage Hover(int relativeIndex, DateTime time)
        => new(new CrosshairPositionData { CandleIndex = relativeIndex, HoveredCandle = new CoreCandleData(time, 1m, 1m, 1m, 1m, 0), ChartSymbol = "TEST" });

    private static CrosshairPositionChangedMessage PointerLeft()
        => new(new CrosshairPositionData { CandleIndex = -1, HoveredCandle = null, ChartSymbol = "TEST" });

    [Fact]
    public void Refresh_AfterAnIndicatorUpdate_KeepsTheHoveredPointAndFigureColumn()
    {
        _chartViewModel.Candles = OriginalCandles(50);   // first: setting the candles clears the converted series when no strategy factory exists
        ArrangePointAndFigureColumns(boxSize: 2m);
        _chartViewModel.VisibleStartIndex = 1;                       // the window starts at the second column
        _sut.Receive(Hover(relativeIndex: 1, new DateTime(2026, 1, 3)));   // third column: 3 boxes
        Assert.Equal("3", _sut.PointAndFigureCountText);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("3", _sut.PointAndFigureCountText);
    }

    [Fact]
    public void Receive_WhenThePointerLeavesTheChart_ShowsTheNewestPointAndFigureColumn()
    {
        _chartViewModel.Candles = OriginalCandles(50);   // first: setting the candles clears the converted series when no strategy factory exists
        ArrangePointAndFigureColumns(boxSize: 2m);
        _sut.Receive(Hover(relativeIndex: 0, new DateTime(2026, 1, 1)));
        Assert.Equal("1", _sut.PointAndFigureCountText);

        _sut.Receive(PointerLeft());

        Assert.Equal("3", _sut.PointAndFigureCountText);
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheNewestPointAndFigureColumn()
    {
        _chartViewModel.Candles = OriginalCandles(50);   // first: setting the candles clears the converted series when no strategy factory exists
        ArrangePointAndFigureColumns(boxSize: 2m);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("3", _sut.PointAndFigureCountText);
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheNewestRenkoBrick()
    {
        // Bricks: up, up, down, down, down -> the newest brick is the third of a run of down bricks.
        _chartViewModel.Candles = OriginalCandles(50);   // first: setting the candles clears the converted series when no strategy factory exists
        var times = Enumerable.Range(0, 5).Select(i => new DateTime(2026, 1, 1).AddDays(i)).ToArray();
        var opens = new[] { 10m, 12m, 14m, 12m, 10m };
        var closes = new[] { 12m, 14m, 12m, 10m, 8m };
        var highs = opens.Zip(closes, Math.Max).ToArray();
        var lows = opens.Zip(closes, Math.Min).ToArray();
        _chartViewModel.EffectiveRenkoBrickSize = 2m;
        _chartViewModel.RenkoReversal = 2;
        _chartViewModel.ChartType = ChartType.Renko;
        _chartViewModel.RenkoDataAdapter = new StockAnalyzer.ZeroAllocation.ChartSegmentAdapter(
            StockAnalyzer.ZeroAllocation.ZeroAllocChartDataType.Renko, times, opens, highs, lows, closes, new long[5],
            Enumerable.Range(0, 5).Select(i => new StockAnalyzer.ZeroAllocation.OriginalIndexRange(i, 1)).ToArray());

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("3", _sut.RenkoCountText);
        Assert.Equal("14.000", _sut.RenkoReversalText);   // down brick: brick high 10 + 2 x brick size 2
        Assert.Equal("6.000", _sut.RenkoUpdateText);      // down brick: brick low 8 - brick size 2
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheNewestKagiColumn()
    {
        // Segments: up 100->110, down 110->105, down 105->95, up 95->120 -> the newest column is the last segment.
        var t = new DateTime(2026, 1, 1);
        _chartViewModel.ChartType = ChartType.Kagi;
        _chartViewModel.ConvertedKagiCandles = new List<CoreCandleData>
        {
            new(t, 100m, 110m, 100m, 110m, 1), new(t.AddDays(1), 110m, 110m, 105m, 105m, 0),
            new(t.AddDays(2), 105m, 105m, 95m, 95m, 0), new(t.AddDays(3), 95m, 120m, 95m, 120m, 1),
        };
        _chartViewModel.Candles = OriginalCandles(50);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("25.000", _sut.KagiReversalText);
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheNewestThreeLineBreakBlock()
    {
        var candles = OriginalCandles(40);   // a steady rise: every new close breaks the previous high
        _chartViewModel.ChartType = ChartType.ThreeLineBreak;
        _chartViewModel.Candles = candles;
        var blocks = StockAnalyzer.Core.Utilities.ThreeLineBreakConverter.Convert(candles, _chartViewModel.ThreeLineBreakLineCount);
        int newestRun = 0;
        for (int i = blocks.Length - 1; i >= 0 && blocks[i].IsUp == blocks[^1].IsUp; i--) newestRun++;
        Assert.True(newestRun > 1);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal(newestRun.ToString(), _sut.ThreeLineBreakCountText);
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheNewestThreeLineBreakBlockEvenWhenTheLastCandleAddsNoBlock()
    {
        // The last candle closes slightly below the previous close, inside the range of the last blocks: it creates no block,
        // so the newest block ends on an earlier date than the newest candle.
        var candles = OriginalCandles(30);
        var last = candles[^1];
        candles.Add(new CoreCandleData(last.Timestamp.AddDays(1), last.Close, last.Close + 0.1m, last.Close - 0.6m, last.Close - 0.5m, 1000));
        _chartViewModel.ChartType = ChartType.ThreeLineBreak;
        _chartViewModel.Candles = candles;
        var blocks = StockAnalyzer.Core.Utilities.ThreeLineBreakConverter.Convert(candles, _chartViewModel.ThreeLineBreakLineCount);
        Assert.NotEqual(candles[^1].Timestamp, blocks[^1].EndDate);
        int newestRun = 0;
        for (int i = blocks.Length - 1; i >= 0 && blocks[i].IsUp == blocks[^1].IsUp; i--) newestRun++;

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal(newestRun.ToString(), _sut.ThreeLineBreakCountText);
    }

    [Fact]
    public void Refresh_WithoutAHover_ShowsTheThreeLineBreakReversalPriceWithThreeDecimals()
    {
        _chartViewModel.ChartType = ChartType.ThreeLineBreak;
        _chartViewModel.Candles = OriginalCandles(40);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Matches(@"^\d+\.\d{3}$", _sut.ThreeLineBreakReversalText);
    }

    [Fact]
    public void Receive_OnReverseWatch_ShowsTheAveragePriceWithThreeDecimals()
    {
        _chartViewModel.ChartType = ChartType.ReverseWatch;
        var point = new StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint
        {
            Date = new DateTime(2026, 1, 2), PriceAverage = 12.3456m, VolumeAverage = 1500m,
            Open = 1m, High = 2m, Low = 0.5m, Close = 1.5m, Volume = 100m, Index = 0
        };

        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 0, ChartSymbol = "TEST", ReverseWatchPoint = point }));

        Assert.Equal("12.346", _sut.ReverseWatchValueText);
        Assert.Equal("1,500", _sut.ReverseWatchVolumeText);
        Assert.Equal("2026-01-02", _sut.DateText);
    }

    // ---- Non-time-series charts: the price rows and the change rows describe the CONVERTED series that the chart draws, not the loaded candles.
    // Series: up 10->12, up 12->14, down 14->12, down 12->10, down 10->8. The loaded candles close at about 100 or higher, far from this range.

    private static List<CoreCandleData> DrawnSeries()
    {
        var opens = new[] { 10m, 12m, 14m, 12m, 10m };
        var closes = new[] { 12m, 14m, 12m, 10m, 8m };
        return Enumerable.Range(0, 5)
            .Select(i => new CoreCandleData(new DateTime(2026, 1, 1).AddDays(i), opens[i], Math.Max(opens[i], closes[i]), Math.Min(opens[i], closes[i]), closes[i], 0))
            .ToList();
    }

    private void ShowDrawnSeries(ChartType chartType, int windowStart = 0)
    {
        var series = DrawnSeries();
        _chartViewModel.Candles = OriginalCandles(50);
        _chartViewModel.ChartType = chartType;
        _chartViewModel.VisibleStartIndex = windowStart;
        _chartViewModel.CurrentSnapshot = new StockAnalyzer.Avalonia.Views.Chart.ChartDataSnapshot(
            series, chartType: chartType, startIndex: windowStart, allCandles: series);
    }

    private static CrosshairPositionChangedMessage HoverElement(int relativeIndex, CoreCandleData element)
        => new(new CrosshairPositionData { CandleIndex = relativeIndex, HoveredCandle = element, ChartSymbol = "TEST" });

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.ThreeLineBreak)]
    public void Receive_OnAnUpElement_ShowsAPositiveChangeFromThePreviousDrawnElement(ChartType chartType)
    {
        ShowDrawnSeries(chartType);

        _sut.Receive(HoverElement(1, DrawnSeries()[1]));   // closes at 14, the previous element closed at 12

        Assert.Equal("+2.000", _sut.YesterdayChangeText);
        Assert.Equal("+16.67%", _sut.YesterdayChangeRatioText);
    }

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.ThreeLineBreak)]
    public void Receive_OnADownElementOfAScrolledWindow_ShowsANegativeChangeFromThePreviousDrawnElement(ChartType chartType)
    {
        ShowDrawnSeries(chartType, windowStart: 2);

        _sut.Receive(HoverElement(1, DrawnSeries()[3]));   // window position 1 + start 2 = element 3: closes at 10, the previous element closed at 12

        Assert.Equal("-2.000", _sut.YesterdayChangeText);
        Assert.Equal("-16.67%", _sut.YesterdayChangeRatioText);
    }

    [Fact]
    public void Receive_OnTheFirstDrawnElement_ShowsNoChange()
    {
        ShowDrawnSeries(ChartType.Renko);

        _sut.Receive(HoverElement(0, DrawnSeries()[0]));

        Assert.Equal(string.Empty, _sut.YesterdayChangeText);
        Assert.Equal(string.Empty, _sut.YesterdayChangeRatioText);
    }

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.ThreeLineBreak)]
    public void Receive_WhenThePointerLeavesTheChart_ShowsTheNewestDrawnElementAndItsChange(ChartType chartType)
    {
        ShowDrawnSeries(chartType);
        _sut.Receive(HoverElement(1, DrawnSeries()[1]));

        _sut.Receive(PointerLeft());

        Assert.Equal("10.000", _sut.OpenText);
        Assert.Equal("10.000", _sut.HighText);
        Assert.Equal("8.000", _sut.LowText);
        Assert.Equal("8.000", _sut.CloseText);
        Assert.Equal("-2.000", _sut.YesterdayChangeText);
        Assert.Equal("-20.00%", _sut.YesterdayChangeRatioText);
    }

    [Fact]
    public void Refresh_WithoutAHoverOnARenkoChart_ShowsTheNewestDrawnElement()
    {
        ShowDrawnSeries(ChartType.Renko);

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("8.000", _sut.CloseText);
        Assert.Equal("-2.000", _sut.YesterdayChangeText);
    }

    [Fact]
    public void Receive_OnATimeSeriesChart_StillComparesWithThePreviousLoadedCandle()
    {
        _chartViewModel.Candles = OriginalCandles(50);
        _chartViewModel.ChartType = ChartType.Candlestick;
        var hovered = OriginalCandles(50)[10];

        _sut.Receive(HoverElement(10, hovered));   // window start 0 + position 10 = loaded candle 10; candle 9 closed one point lower

        Assert.Equal("+1.000", _sut.YesterdayChangeText);
    }

    [Fact]
    public void Receive_OnATimeSeriesChartWithANegativePreviousClose_ShowsTheRatioLikeEveryOtherScreen()
    {
        // Owner decision 2026-10-14: the ratio is Change / previous close * 100 whenever the previous close is not 0 (negative prices exist, e.g. crude oil futures).
        _chartViewModel.Candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 4, 20), -45m, -35m, -45m, -40m, 0),
            new(new DateTime(2026, 4, 21), -40m, -15m, -40m, -20m, 0),
        };
        _chartViewModel.ChartType = ChartType.Candlestick;

        _sut.Receive(HoverElement(1, _chartViewModel.Candles[1]));

        Assert.Equal("+20.000", _sut.YesterdayChangeText);
        Assert.Equal("-50.00%", _sut.YesterdayChangeRatioText);
    }

    [Fact]
    public void Receive_WhenTheHoveredPositionIsBeyondTheDrawnSeries_ShowsTheNewestDrawnElementWithItsOwnChange()
    {
        ShowDrawnSeries(ChartType.Renko);   // five elements
        var unrelated = new CoreCandleData(new DateTime(2030, 1, 1), 99m, 99m, 99m, 99m, 0);

        _sut.Receive(HoverElement(9, unrelated));

        Assert.Equal("8.000", _sut.CloseText);
        Assert.Equal("-2.000", _sut.YesterdayChangeText);
    }

    [Fact]
    public void Refresh_AfterAHoverWithoutACandleOnATimeSeriesChart_ComparesTheNewestCandleWithTheOneBeforeIt()
    {
        // The crosshair reported a position but no candle (the pointer between columns). A later refresh without a hover must not reuse that stale position.
        _chartViewModel.Candles = OriginalCandles(50);
        _chartViewModel.ChartType = ChartType.Candlestick;
        _sut.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData { CandleIndex = 3, HoveredCandle = null, ChartSymbol = "TEST" }));

        _messenger.Send(new IndicatorResultsUpdatedMessage());

        Assert.Equal("149.500", _sut.CloseText);
        Assert.Equal("+1.000", _sut.YesterdayChangeText);
    }

    // ---- Kagi: the unit is the column (a run of segments in one direction), so the change is the signed move of the column and equals the Move row.
    // Segments: up 100->110, up 110->120 (the same column), down 120->105, down 105->95 (one column), up 95->130.

    private static List<CoreCandleData> KagiSegments()
    {
        var t = new DateTime(2026, 1, 1);
        return new List<CoreCandleData>
        {
            new(t, 100m, 110m, 100m, 110m, 0), new(t.AddDays(1), 110m, 120m, 110m, 120m, 1),
            new(t.AddDays(2), 120m, 120m, 105m, 105m, 0), new(t.AddDays(3), 105m, 105m, 95m, 95m, 0),
            new(t.AddDays(4), 95m, 130m, 95m, 130m, 1),
        };
    }

    private void ShowKagiSegments()
    {
        var segments = KagiSegments();
        _chartViewModel.Candles = OriginalCandles(50);
        _chartViewModel.ChartType = ChartType.Kagi;
        _chartViewModel.ConvertedKagiCandles = segments;
        _chartViewModel.VisibleStartIndex = 0;
        _chartViewModel.CurrentSnapshot = new StockAnalyzer.Avalonia.Views.Chart.ChartDataSnapshot(
            segments, chartType: ChartType.Kagi, startIndex: 0, allCandles: segments);
    }

    [Theory]
    [InlineData(1, "+20.000", "+20.00%", "20.000")]    // the last segment of the first column: 100 -> 120, not only the last segment 110 -> 120
    [InlineData(0, "+20.000", "+20.00%", "20.000")]    // the same column hovered on its first segment
    [InlineData(3, "-25.000", "-20.83%", "25.000")]    // second column: 120 -> 95
    [InlineData(4, "+35.000", "+36.84%", "35.000")]    // a column of one segment: 95 -> 130
    public void Receive_OnKagi_ChangeIsTheSignedMoveOfTheColumn(int segment, string change, string ratio, string move)
    {
        ShowKagiSegments();

        _sut.Receive(HoverElement(segment, KagiSegments()[segment]));

        Assert.Equal(change, _sut.YesterdayChangeText);
        Assert.Equal(ratio, _sut.YesterdayChangeRatioText);
        Assert.Equal(move, _sut.KagiReversalText);
    }

    [Fact]
    public void Receive_WhenThePointerLeavesAKagiChart_ShowsTheNewestColumnWithMatchingChangeAndMove()
    {
        ShowKagiSegments();

        _sut.Receive(PointerLeft());

        Assert.Equal("130.000", _sut.CloseText);
        Assert.Equal("+35.000", _sut.YesterdayChangeText);
        Assert.Equal("35.000", _sut.KagiReversalText);
    }

    private class DummyMissingKeyLocalizationService : StockAnalyzer.Core.Interfaces.ILocalizationService
    {
        public string GetString(string key) => $"[{key}]";
    }
}
