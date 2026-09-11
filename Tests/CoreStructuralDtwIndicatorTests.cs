using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Tests.Models.Indicators.Advanced
{
    public class CoreStructuralDtwIndicatorTests
    {
        private class MockExecutionContext : IExecutionContext
        {
            public IPythonService PythonService { get; set; }
        }

        private class MockPythonService : IPythonService
        {
            public string OscillatorResponseJson { get; set; } = @"{""status"":""success"",""result"":[null,null,1.0,0.5,0.0]}";
            public List<CandleData> ReceivedCandles { get; private set; }

            public bool IsInitializing => false;
            public Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
            public Task InitializeExternalProcessAsync() => Task.CompletedTask;
            public Task<string> PingExternalProcessAsync() => Task.FromResult("pong");
            public Task<string> SendCandlesAsync(List<CandleData> candles)
            {
                ReceivedCandles = candles;
                return Task.FromResult("success");
            }
            public Task<string> CalculateEgarchAsync(int p = 1, int q = 1) => throw new NotImplementedException();
            public Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m) => throw new NotImplementedException();
            public Task<string> CalculateFftCycleAsync(int windowSize = 256) => throw new NotImplementedException();
            public Task<string> CalculateFourierTransformAsync(int targetPeriod = 50) => throw new NotImplementedException();
            public Task<string> CalculateFftTrendFilterAsync(int windowSize = 256, int numHarmonics = 3) => throw new NotImplementedException();
            public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => throw new NotImplementedException();
            public Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = 14, double shortSpanPenaltyAlpha = 0.5) => throw new NotImplementedException();
            public Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = 14) => throw new NotImplementedException();
            public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = 14) => throw new NotImplementedException();
            
            public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = 14)
            {
                return Task.FromResult(OscillatorResponseJson);
            }

            public Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, CancellationToken ct = default) => Task.CompletedTask;
            public Task<T> RunAsync<T>(Func<Python.Runtime.PyModule, T> func, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        }

        [Fact]
        public async Task CalculateAsync_WithoutPythonService_ReturnsFailure()
        {
            var indicator = new CoreStructuralDtwIndicator();
            var context = new MockExecutionContext { PythonService = null };
            var candles = new List<CoreCandleData> { new CoreCandleData(DateTime.Now, 100m, 100m, 100m, 100m, 0) };

            var result = await indicator.CalculateAsync(candles, context);

            Assert.False(result.IsSuccessful);
            Assert.Contains("requires IPythonService", result.ErrorMessage);
        }

        [Fact]
        public async Task CalculateAsync_WithNotEnoughData_ReturnsNullArray()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 14, Lag = 14 };
            var context = new MockExecutionContext { PythonService = new MockPythonService() };
            
            // Only 10 candles, but needs Period + Lag (28)
            var candles = Enumerable.Range(0, 10).Select(i => new CoreCandleData(DateTime.Now.AddDays(i), 100m, 100m, 100m, 100m, 0)).ToList();

            var result = await indicator.CalculateAsync(candles, context);

            Assert.True(result.IsSuccessful);
            Assert.Single(result.SeriesNames);
            var series = result.GetSeries(IndicatorResult.MainSeriesName);
            Assert.Equal(10, series.Count());
            Assert.All(series, item => Assert.Null(item));
        }

        [Fact]
        public async Task CalculateAsync_WithSufficientData_ReturnsParsedResults()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };
            var pythonService = new MockPythonService();
            var context = new MockExecutionContext { PythonService = pythonService };
            
            // Need at least 4 candles
            var candles = Enumerable.Range(0, 5).Select(i => new CoreCandleData(DateTime.Now.AddDays(i), 100m, 100m + i, 100m, 100m + i, 0)).ToList();

            var result = await indicator.CalculateAsync(candles, context);

            Assert.True(result.IsSuccessful);
            Assert.Single(result.SeriesNames);
            
            var series = result.GetSeries(IndicatorResult.MainSeriesName);
            
            // Result is [null, null, 1.0, 0.5, 0.0] as mocked
            Assert.Equal(5, series.Count());
            Assert.Null(series[0]);
            Assert.Null(series[1]);
            Assert.Equal(1.0m, series[2]);
            Assert.Equal(0.5m, series[3]);
            Assert.Equal(0.0m, series[4]);

            // Ensure SendCandlesAsync was called with properly mapped candles
            Assert.NotNull(pythonService.ReceivedCandles);
            Assert.Equal(5, pythonService.ReceivedCandles.Count);
            Assert.Equal(104m, pythonService.ReceivedCandles[4].Close);
        }

        [Fact]
        public async Task CalculateAsync_WithPythonError_ReturnsFailure()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };
            var pythonService = new MockPythonService
            {
                OscillatorResponseJson = @"{""status"":""error"",""error"":""Some python error""}"
            };
            var context = new MockExecutionContext { PythonService = pythonService };
            var candles = Enumerable.Range(0, 5).Select(i => new CoreCandleData(DateTime.Now.AddDays(i), 100m, 100m, 100m, 100m, 0)).ToList();

            var result = await indicator.CalculateAsync(candles, context);

            Assert.False(result.IsSuccessful);
            Assert.Equal("Some python error", result.ErrorMessage);
        }
    }
}
