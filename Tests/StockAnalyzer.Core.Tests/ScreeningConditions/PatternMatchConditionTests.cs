using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ScreeningConditions;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.ScreeningConditions;

public class PatternMatchConditionTests
{
    private class MockPythonService : IPythonService
    {
        private readonly string _detectResponse;

        public MockPythonService(string detectResponse)
        {
            _detectResponse = detectResponse;
        }

        public bool IsInitializing => false;
        public Task InitializeAsync(System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task InitializeExternalProcessAsync() => Task.CompletedTask;
        public Task<string> PingExternalProcessAsync() => Task.FromResult("pong");
        public Task<string> SendCandlesAsync(List<CandleData> candles)
            => Task.FromResult("{\"status\":\"transfer_complete\",\"rows\":50}");
        public Task<string> CalculateEgarchAsync(int p = 1, int q = 1) => throw new NotImplementedException();
        public Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m) => throw new NotImplementedException();
        public Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize) => throw new NotImplementedException();
        public Task<string> CalculateFourierTransformAsync(int targetPeriod = ChartConstants.FourierTransformDefaultTargetPeriod) => throw new NotImplementedException();
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics) => throw new NotImplementedException();
        public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => throw new NotImplementedException();
        public Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha)
            => Task.FromResult(_detectResponse);
        public Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius) => throw new NotImplementedException();
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius) => throw new NotImplementedException();
        public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius) => throw new NotImplementedException();
        public Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> RunAsync<T>(Func<Python.Runtime.PyModule, T> func, System.Threading.CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private static List<CandleData> CreateSampleCandles(int count = 50)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(
                DateTime.Now.AddDays(-count + i),
                100m + i, 105m + i, 95m + i, 100m + i, 1000
            ));
        }
        return candles;
    }

    [Fact]
    public async Task IsMetAsync_DetectsPattern_ReturnsTrue()
    {
        var jsonResponse = @"{
            ""status"": ""ok"",
            ""result"": {
                ""patterns"": [
                    { ""name"": ""Double Bottom"", ""probability"": 0.85, ""startIndex"": 10, ""endIndex"": 40 }
                ]
            }
        }";

        var mockPython = new MockPythonService(jsonResponse);
        var prs = new PatternRecognitionService(mockPython);
        var condition = new PatternMatchCondition(prs, "Double Bottom", 0.8);

        var candles = CreateSampleCandles();
        var result = await condition.IsMetAsync(candles);

        Assert.True(result);
    }

    [Fact]
    public async Task IsMetAsync_NoPatterns_ReturnsFalse()
    {
        var jsonResponse = @"{
            ""status"": ""ok"",
            ""result"": { ""patterns"": [] }
        }";

        var mockPython = new MockPythonService(jsonResponse);
        var prs = new PatternRecognitionService(mockPython);
        var condition = new PatternMatchCondition(prs, "Double Top", 0.5);

        var candles = CreateSampleCandles();
        var result = await condition.IsMetAsync(candles);

        Assert.False(result);
    }

    [Fact]
    public async Task IsMetAsync_LowProbabilityPattern_ReturnsFalse()
    {
        var jsonResponse = @"{
            ""status"": ""ok"",
            ""result"": {
                ""patterns"": [
                    { ""name"": ""Head and Shoulders"", ""probability"": 0.6, ""startIndex"": 5, ""endIndex"": 45 }
                ]
            }
        }";

        var mockPython = new MockPythonService(jsonResponse);
        var prs = new PatternRecognitionService(mockPython);
        var condition = new PatternMatchCondition(prs, "Head and Shoulders", 0.7);

        var candles = CreateSampleCandles();
        var result = await condition.IsMetAsync(candles);

        Assert.False(result);
    }

    [Fact]
    public async Task IsMetAsync_PythonError_ReturnsFalse()
    {
        var jsonResponse = @"{
            ""status"": ""error"",
            ""error"": ""Calculation failed""
        }";

        var mockPython = new MockPythonService(jsonResponse);
        var prs = new PatternRecognitionService(mockPython);
        var condition = new PatternMatchCondition(prs);

        var candles = CreateSampleCandles();
        var result = await condition.IsMetAsync(candles);

        Assert.False(result);
    }
}
