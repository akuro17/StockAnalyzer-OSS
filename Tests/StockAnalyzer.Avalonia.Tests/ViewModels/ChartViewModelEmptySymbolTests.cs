using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Implementation task (EmptyInitialTicker): with no symbol (empty Chart:DefaultSymbol on first launch) the chart
/// must not fetch anything and must stay empty until the user enters a ticker.
/// </summary>
public sealed class ChartViewModelEmptySymbolTests
{
    private static ChartViewModel BuildChart(CountingDataService dataService)
    {
        return new ChartViewModel(
            dataService,
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(dataService),
            null!,
            new StockAnalyzer.Core.Theme.ThemeManager(),
            new MockChartSettingsManager(),
            new SynchronousDispatcherService(),
            null,
            null!,
            null,
            null,
            null,
            messenger: new StrongReferenceMessenger());
    }

    [Fact]
    public async Task LoadData_WithEmptySymbol_DoesNotFetchAndKeepsChartEmpty()
    {
        var dataService = new CountingDataService();
        var chart = BuildChart(dataService);
        chart.Symbol = string.Empty;
        var callsBefore = dataService.CallCount;

        await chart.LoadDataAsync();

        Assert.Equal(callsBefore, dataService.CallCount);
        Assert.Empty(chart.Candles);
        Assert.False(chart.IsLoading);
    }

    [Fact]
    public async Task LoadData_AfterSymbolIsEntered_FetchesForThatSymbol()
    {
        var dataService = new CountingDataService();
        var chart = BuildChart(dataService);
        chart.Symbol = string.Empty;

        chart.Symbol = "GOOG";
        await chart.LoadDataAsync();

        Assert.Contains("GOOG", dataService.Symbols);
    }

    private sealed class CountingDataService : IDataService
    {
        private int _callCount;
        private readonly List<string> _symbols = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string> Symbols
        {
            get { lock (_symbols) { return _symbols.ToArray(); } }
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            Interlocked.Increment(ref _callCount);
            lock (_symbols) { _symbols.Add(symbol); }
            var candles = new List<CandleData>
            {
                new CandleData(new DateTime(2024, 1, 2), 10m, 11m, 9m, 10.5m, 100),
            };
            return Task.FromResult<IReadOnlyList<CandleData>>(candles);
        }
    }
}
