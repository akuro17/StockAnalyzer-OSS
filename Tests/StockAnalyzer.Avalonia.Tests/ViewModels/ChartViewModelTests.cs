using Xunit;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class ChartViewModelTests
{
    private readonly ChartViewModel _sut;

    public ChartViewModelTests()
    {
        var settingsManager = new StockAnalyzer.Avalonia.Services.MockChartSettingsManager();
        settingsManager.UpdatePreview(new StockAnalyzer.Core.Models.Settings.GlobalChartSettings { IsSubWindowVisible = false });

        // Use a private messenger to avoid interference with other tests
        _sut = new ChartViewModel(
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
            messenger: new StrongReferenceMessenger()
        );
    }

    [Fact]
    public async Task ChartTypeChanged_ToRelativePerformance_IncrementsRenderTrigger()
    {
        // Arrange
        _sut.ChartType = ChartType.Candlestick;
        int initialTrigger = _sut.RenderTrigger;

        // Act
        _sut.ChartType = ChartType.RelativePerformance;

        // Assert
        // CalculateComparisonDataAsync runs in a Task.Run and updates via dispatcher.
        // We wait briefly for the async operation to complete (debounce 150ms + load).
        await Task.Delay(1500);
        
        // This test is expected to pass now.
        Assert.True(_sut.RenderTrigger > initialTrigger, 
            $"RenderTrigger should have incremented. Initial: {initialTrigger}, Current: {_sut.RenderTrigger}");
    }

    [Fact]
    public async Task ComparisonTargetsChanged_IncrementsRenderTrigger()
    {
        // Arrange
        _sut.ChartType = ChartType.RelativePerformance;
        await Task.Delay(1500); // Wait for initial load
        int initialTrigger = _sut.RenderTrigger;

        // Act
        _sut.ComparisonTargets.Add("AAPL");

        // Assert
        await Task.Delay(1500);
        
        // This test is expected to pass now.
        Assert.True(_sut.RenderTrigger > initialTrigger, 
            $"RenderTrigger should have incremented after adding a comparison target. Initial: {initialTrigger}, Current: {_sut.RenderTrigger}");
    }

    [Fact]
    public void DisplayModeChanges_FiresOnChartTypeChange()
    {
        // Arrange
        ChartType emittedType = ChartType.Candlestick;
        int fireCount = 0;
        using var subscription = _sut.DisplayModeChanges.Subscribe(t => 
        {
            emittedType = t;
            fireCount++;
        });

        // Act
        _sut.ChartType = ChartType.Renko;

        // Assert
        Assert.Equal(1, fireCount);
        Assert.Equal(ChartType.Renko, emittedType);
    }

    [Fact]
    public void IsSubWindowVisible_DefaultIsOff()
    {
        // Assert: Default should be OFF (false)
        Assert.False(_sut.IsSubWindowVisible);
    }

    [Fact]
    public void IsSubWindowVisible_CanToggleOn()
    {
        // Act
        _sut.IsSubWindowVisible = true;

        // Assert
        Assert.True(_sut.IsSubWindowVisible);
    }

    [Fact]
    public void IsSubWindowVisible_CanToggleOff()
    {
        // Arrange
        _sut.IsSubWindowVisible = true;

        // Act
        _sut.IsSubWindowVisible = false;

        // Assert
        Assert.False(_sut.IsSubWindowVisible);
    }

    [Fact]
    public void IsSubWindowVisible_IncrementsRenderTrigger()
    {
        // Arrange
        int initial = _sut.RenderTrigger;

        // Act
        _sut.IsSubWindowVisible = true;

        // Assert
        Assert.True(_sut.RenderTrigger > initial);
    }
    [Fact]
    public void IsSyncEnabled_DefaultIsTrue()
    {
        Assert.True(_sut.IsSyncEnabled);
    }

    [Fact]
    public void Receive_TickerSelectedMessage_UpdatesSymbol_WhenSyncEnabled()
    {
        // Arrange
        _sut.IsSyncEnabled = true;
        _sut.Symbol = "AAPL";
        var message = new TickerSelectedMessage("MSFT");

        // Act
        _sut.Receive(message);

        // Assert
        Assert.Equal("MSFT", _sut.Symbol);
    }

    [Fact]
    public void Receive_TickerSelectedMessage_DoesNotUpdateSymbol_WhenSyncDisabled()
    {
        // Arrange
        _sut.IsSyncEnabled = false;
        _sut.Symbol = "AAPL";
        var message = new TickerSelectedMessage("MSFT");

        // Act
        _sut.Receive(message);

        // Assert
        Assert.Equal("AAPL", _sut.Symbol);
    }

    [Fact]
    public void TabHeaderText_ReflectsSyncState()
    {
        // Arrange
        _sut.Symbol = "AAPL";

        // Act & Assert
        _sut.IsSyncEnabled = true;
        Assert.Equal("AAPL", _sut.TabHeaderText);

        _sut.IsSyncEnabled = false;
        Assert.Equal("★AAPL", _sut.TabHeaderText);
    }

    // --- JumpToAnchorDate (spec section 6.2, Step 90-1-17) ---

    private static CoreCandleData MakeCandle(DateTime timestamp) => new(timestamp, 100m, 101m, 99m, 100m, 1000);

    [Fact]
    public void JumpToAnchorDate_WithNoCandlesLoaded_ReturnsFalseAndLeavesVisibleStartIndexUnchanged()
    {
        _sut.Candles = Array.Empty<CoreCandleData>();
        _sut.VisibleStartIndex = 5;

        var result = _sut.JumpToAnchorDate(new DateTime(2026, 8, 12), TimeFrame.D1);

        Assert.False(result);
        Assert.Equal(5, _sut.VisibleStartIndex);
    }

    [Fact]
    public void JumpToAnchorDate_CentersVisibleStartIndexOnTheResolvedAnchor()
    {
        // 100 daily candles starting 2026-01-01; VisibleCandleCount defaults from ChartConstants,
        // pin it explicitly here so the expected center is deterministic regardless of that default.
        _sut.Candles = Enumerable.Range(0, 100)
            .Select(i => MakeCandle(new DateTime(2026, 1, 1).AddDays(i)))
            .ToArray();
        _sut.VisibleCandleCount = 20;

        var anchorDate = new DateTime(2026, 1, 1).AddDays(50); // index 50, well within bounds
        var result = _sut.JumpToAnchorDate(anchorDate, TimeFrame.D1);

        Assert.True(result);
        Assert.Equal(40, _sut.VisibleStartIndex); // 50 - (20 / 2)
    }

    [Fact]
    public void JumpToAnchorDate_ClampsToZero_WhenAnchorIsNearTheStartOfTheSeries()
    {
        _sut.Candles = Enumerable.Range(0, 100)
            .Select(i => MakeCandle(new DateTime(2026, 1, 1).AddDays(i)))
            .ToArray();
        _sut.VisibleCandleCount = 20;

        var anchorDate = new DateTime(2026, 1, 1); // index 0 - centering would go negative
        var result = _sut.JumpToAnchorDate(anchorDate, TimeFrame.D1);

        Assert.True(result);
        Assert.Equal(0, _sut.VisibleStartIndex);
    }

    [Fact]
    public void JumpToAnchorDate_ClampsToSeriesEnd_WhenAnchorIsNearTheEndOfTheSeries()
    {
        _sut.Candles = Enumerable.Range(0, 100)
            .Select(i => MakeCandle(new DateTime(2026, 1, 1).AddDays(i)))
            .ToArray();
        _sut.VisibleCandleCount = 20;

        var anchorDate = new DateTime(2026, 1, 1).AddDays(99); // last index - centering would overrun
        var result = _sut.JumpToAnchorDate(anchorDate, TimeFrame.D1);

        Assert.True(result);
        Assert.Equal(80, _sut.VisibleStartIndex); // clamped to Count - VisibleCandleCount
    }

    [Fact]
    public void JumpToAnchorDate_HolidayAnchorDate_FallsBackToNearestPastTradingDay()
    {
        // Candles for Mon-Fri only (2026-08-03 is a Monday); Saturday 08-08 has no candle.
        _sut.Candles = new[]
        {
            MakeCandle(new DateTime(2026, 8, 3)),
            MakeCandle(new DateTime(2026, 8, 4)),
            MakeCandle(new DateTime(2026, 8, 5)),
            MakeCandle(new DateTime(2026, 8, 6)),
            MakeCandle(new DateTime(2026, 8, 7)),
        };
        _sut.VisibleCandleCount = 20;

        var result = _sut.JumpToAnchorDate(new DateTime(2026, 8, 8), TimeFrame.D1);

        Assert.True(result);
        Assert.Equal(0, _sut.VisibleStartIndex); // clamped: series shorter than VisibleCandleCount
    }
}

