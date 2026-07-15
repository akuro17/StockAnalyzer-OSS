using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerServiceTests
{
    private class StubSettings : IStockAnalyzerSettings
    {
        public string? PythonPath => null;
        public string PythonScriptDirectory => "Scripts";
        public string PythonServerScriptName => "server.py";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1000;
        public int PythonHealthCheckIntervalMs => 5000;
        public string DefaultSymbol => "MSFT";
        public string RenkoUpColor => "Green";
        public string RenkoDownColor => "Red";
        public string KagiUpColor => "Green";
        public string KagiDownColor => "Red";
        public string PnfUpColor => "Green";
        public string PnfDownColor => "Red";
        public string? ScreeningDataPath => null;
        // Infrastructure
        public string PipeName => "ScreenerTestPipe";
        public int PipeConnectionTimeoutMs { get; set; } = 5000;
        public int ScreenerMaxParallelism { get; set; } = 3;

        // Pattern Recognition
        public int PatternRecognitionMinWindow => 20;
        public int PatternRecognitionMaxWindow => 60;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;

        // Market Structure
        public decimal ZigzagThresholdPercent => 5.0m;

        // Localization
        public string? LocaleResourcePath => null;
        public string GetReverseWatchPhaseColor(int phase) => "#808080";
        public IReadOnlyList<string> DefaultScreenerSymbols => System.Array.Empty<string>();
        // Resilience (Circuit Breaker)
        public int CircuitBreakerMinimumThroughput => 3;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public int PipeConnectPollIntervalMs => 100;
        public int DisposeWaitMs => 1000;
        public int SyncTimeoutMinutes => 2;
        public IReadOnlyList<string> PythonEssentialPackages => new[] { "setuptools", "wheel", "polars", "pandas", "scipy", "yfinance", "pyarrow", "pandas-ta", "scikit-learn", "arch", "statsmodels", "pywin32", "tslearn" };
        public string PredictionModelPath => "Models/trend_predictor.onnx";
        public int PredictionWindowSize => 10;
    }

    private readonly IStockAnalyzerSettings _stubSettings = new StubSettings();
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MockPythonServiceBase _mockPythonService;
    private readonly ParquetMarketDataProvider _parquetProvider;
    private readonly ScreenerService _service;

    public ScreenerServiceTests()
    {
        _dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        _mockPythonService = new MockPythonServiceBase();
        var settings = Microsoft.Extensions.Options.Options.Create(new MarketDataSettings 
        { 
            DailyDataPath = System.IO.Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily")
        });
        _parquetProvider = new ParquetMarketDataProvider(_dbManager, _mockPythonService, settings, NullLogger<ParquetMarketDataProvider>.Instance);
        
        var providers = new List<IMarketDataProvider> { _parquetProvider };
        var mockDataService = new MockDataService(symbol => Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>()));
        _service = new ScreenerService(mockDataService, _stubSettings, providers);
    }

    private class MockDataService : IDataService
    {
        private readonly Func<string, Task<IReadOnlyList<CandleData>>> _loader;

        public MockDataService(Func<string, Task<IReadOnlyList<CandleData>>> loader)
        {
            _loader = loader;
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            return _loader(symbol);
        }
    }

    private class MockScreeningCondition : IScreeningCondition
    {
        private readonly Func<IReadOnlyList<CandleData>, bool> _evaluator;

        public MockScreeningCondition(Func<IReadOnlyList<CandleData>, bool> evaluator)
        {
            _evaluator = evaluator;
        }

        public bool IsMet(IReadOnlyList<CandleData> candles)
        {
            return _evaluator(candles);
        }
    }

    [Fact]
    public async Task ScreenAsync_ShouldReturnMatchedSymbols_WhenConditionIsMet()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG", "AAPL" };
        var dataService = new MockDataService(symbol => Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>()));
        var condition = new MockScreeningCondition(candles => true); // Always match
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Equal(symbols.Count, result.Count);
        Assert.True(symbols.All(s => result.Contains(s)));
    }

    [Fact]
    public async Task ScreenAsync_ShouldHandleEmptySymbolList()
    {
        // Arrange
        var symbols = new List<string>();
        var dataService = new MockDataService(_ => throw new Exception("Should not be called"));
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScreenAsync_ShouldReportProgressCorrectly()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG" };
        var dataService = new MockDataService(async symbol =>
        {
            await Task.Delay(50); // Small delay
            return new List<CandleData>();
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        
        var progressValues = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var tcs = new TaskCompletionSource<bool>();
        var progress = new Progress<int>(p => 
        {
            progressValues.Enqueue(p);
            if (p == 100) tcs.TrySetResult(true);
        });

        // Act
        await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Wait for the 100% progress report to be processed
        await Task.WhenAny(tcs.Task, Task.Delay(1000));

        // Assert
        Assert.Contains(50, progressValues);
        Assert.Contains(100, progressValues);
        Assert.Equal(100, progressValues.Last());
    }

    [Fact]
    public async Task ScreenAsync_ShouldRespectCancellationToken()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG", "AAPL" };
        var cts = new CancellationTokenSource();
        var dataService = new MockDataService(async symbol =>
        {
            await Task.Delay(100, cts.Token);
            return new List<CandleData>();
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act & Assert
        var screenTask = service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => screenTask);
    }

    [Fact]
    public async Task ScreenAsync_ShouldContinueWhenOneSymbolFails()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "FAIL", "AAPL" };
        var dataService = new MockDataService(symbol =>
        {
            if (symbol == "FAIL") throw new Exception("Simulated network error");
            return Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>());
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Contains("MSFT", result);
        Assert.Contains("AAPL", result);
        Assert.DoesNotContain("FAIL", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ScreenAsync_WithCriteria_ShouldUseParquetProvider()
    {
        // Arrange
        // The _service is now initialized in the constructor with _parquetProvider
        var criteria = new ScreeningCriteria
        {
            Conditions = new List<IScreeningCondition>
            {
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 100m)
            }
        };

        // Act
        var result = await _service.ScreenAsync(criteria, new Progress<int>(), CancellationToken.None);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("MSFT", result);
    }

    [Fact]
    public async Task GetMetadataAsync_ShouldReturnUnknown_WhenPythonFails()
    {
        // Act
        var result = await _parquetProvider.GetMetadataAsync("UNKNOWN_TICKER");

        // Assert
        Assert.Equal("Unknown", result.Sector);
        Assert.Equal("Unknown", result.Industry);
    }
}
