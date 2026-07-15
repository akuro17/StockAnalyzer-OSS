using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest
{
    public class BacktestStatisticsCalculatorTests : IAsyncLifetime
    {
        private readonly PythonService _pythonService;
        private readonly BacktestStatisticsCalculator _calculator;

        public BacktestStatisticsCalculatorTests()
        {
            var settings = new StubScreenerSettings();
            _pythonService = new PythonService(settings);
            _calculator = new BacktestStatisticsCalculator(_pythonService);
        }

        [Fact]
        public async Task CalculateAsync_WithValidTrades_ReturnsCorrectStatistics()
        {
            // Arrange
            var trades = new List<Trade>
            {
                new Trade { EntryPrice = 100, ExitPrice = 110, Quantity = 1, ProfitLoss = 10 },
                new Trade { EntryPrice = 110, ExitPrice = 105, Quantity = 1, ProfitLoss = -5 },
                new Trade { EntryPrice = 105, ExitPrice = 120, Quantity = 1, ProfitLoss = 15 },
                new Trade { EntryPrice = 120, ExitPrice = 118, Quantity = 1, ProfitLoss = -2 },
                new Trade { EntryPrice = 118, ExitPrice = 130, Quantity = 1, ProfitLoss = 12 }
            }; // Total PnL = 30. Wins = 10, 15, 12 (sum 37). Losses = -5, -2 (sum -7). Profit Factor = 37 / 7 = 5.28

            // Act
            var stats = await _calculator.CalculateAsync(trades);

            // Assert
            Assert.NotNull(stats);
            Assert.Equal(5, stats.TotalTrades);
            Assert.Equal(3.0 / 5.0, stats.WinRate, 4);
            Assert.Equal(30, stats.TotalProfit);
            Assert.Equal(37.0 / 3.0, stats.AverageProfit, 4);
            Assert.Equal(-7.0 / 2.0, stats.AverageLoss, 4);
            Assert.True(stats.MaxDrawdown > 0);
            Assert.True(stats.TradeSharpeRatio > 0);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_pythonService != null)
            {
                await _pythonService.DisposeAsync();
            }
        }

        private class StubScreenerSettings : IStockAnalyzerSettings
        {
            public string? PythonPath => null;
            public string PythonScriptDirectory => "../../../../../StockAnalyzer.Core/Scripts";
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
            public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false)
                => Task.FromResult("{}");
            public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14)
                => Task.FromResult("{}");
            public string? ScreeningDataPath => null;
            public string PipeName => "StockAnalyzerPipe";
            public int PipeConnectionTimeoutMs => 5000;
            public int ScreenerMaxParallelism => 10;
            public int PatternRecognitionMinWindow => 20;
            public int PatternRecognitionMaxWindow => 60;
            public int PatternRecognitionWindowStep => 5;
            public double PatternRecognitionDefaultThreshold => 0.5;
            
            public decimal ZigzagThresholdPercent => 5.0m;
            
            public string? LocaleResourcePath => null;
            public string GetReverseWatchPhaseColor(int phase) => "#808080";
            public IReadOnlyList<string> DefaultScreenerSymbols => Array.Empty<string>();
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
    }
}
