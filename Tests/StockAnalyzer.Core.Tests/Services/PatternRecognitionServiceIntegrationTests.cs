using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Integration tests for the PatternRecognitionService.
/// Uses a MockPythonService to test the full JSON roundtrip without requiring Python.
/// </summary>
public class PatternRecognitionServiceIntegrationTests
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
            => Task.FromResult("{\"status\":\"transfer_complete\",\"rows\":" + candles.Count + "}");
        public Task<string> CalculateEgarchAsync(int p = 1, int q = 1)
            => Task.FromResult("{}");
        public Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m)
            => Task.FromResult("{}");
        public Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize)
            => Task.FromResult("{}");
        public Task<string> CalculateFourierTransformAsync(int targetPeriod = ChartConstants.FourierTransformDefaultTargetPeriod)
            => Task.FromResult("{}");
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics)
            => Task.FromResult("{}");
        public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades)
            => Task.FromResult("{}");
        public Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha)
            => Task.FromResult(_detectResponse);
        public Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult("{}");
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult("{}");
        public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult("{}");
        public Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, System.Threading.CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<T> RunAsync<T>(Func<Python.Runtime.PyModule, T> func, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(default(T)!);
    }

    private static List<CandleData> CreateSampleCandles(int count = 100)
    {
        var candles = new List<CandleData>();
        var random = new Random(42); // Fixed seed for reproducibility
        decimal basePrice = 100m;

        for (int i = 0; i < count; i++)
        {
            decimal change = (decimal)(random.NextDouble() - 0.5) * 5;
            decimal open = basePrice + change;
            decimal high = open + (decimal)random.NextDouble() * 3;
            decimal low = open - (decimal)random.NextDouble() * 3;
            decimal close = low + (decimal)random.NextDouble() * (high - low);
            basePrice = close;

            candles.Add(new CandleData(
                DateTime.Now.AddDays(-count + i),
                open, high, low, close, 1000000 + random.Next(500000)));
        }
        return candles;
    }

    [Fact]
    public async Task FullPipeline_MultiplePatterns_ParsesCorrectly()
    {
        // Simulates a Python response with multiple detected patterns
        var json = @"{
            ""status"": ""ok"",
            ""result"": {
                ""patterns"": [
                    {""name"": ""DoubleBottom"", ""probability"": 0.85, ""startIndex"": 45, ""endIndex"": 78},
                    {""name"": ""HeadAndShoulders"", ""probability"": 0.72, ""startIndex"": 10, ""endIndex"": 50},
                    {""name"": ""TripleTop"", ""probability"": 0.61, ""startIndex"": 60, ""endIndex"": 95}
                ]
            }
        }";
        var service = new PatternRecognitionService(new MockPythonService(json));
        var candles = CreateSampleCandles();

        var result = await service.DetectAsync(candles);

        Assert.True(result.IsSuccessful);
        Assert.Equal(3, result.Patterns.Count);

        // Verify first pattern
        Assert.Equal("DoubleBottom", result.Patterns[0].Name);
        Assert.Equal(0.85, result.Patterns[0].Probability);
        Assert.Equal(45, result.Patterns[0].StartIndex);
        Assert.Equal(78, result.Patterns[0].EndIndex);

        // Verify third pattern
        Assert.Equal("TripleTop", result.Patterns[2].Name);
        Assert.Equal(0.61, result.Patterns[2].Probability);
    }

    [Fact]
    public async Task FullPipeline_NoPatterns_ReturnsEmptyList()
    {
        var json = @"{""status"": ""ok"", ""result"": {""patterns"": []}}";
        var service = new PatternRecognitionService(new MockPythonService(json));

        var result = await service.DetectAsync(CreateSampleCandles());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public async Task FullPipeline_PythonError_ReturnsFailure()
    {
        var json = @"{""status"": ""error"", ""error"": ""Required package not installed: tslearn""}";
        var service = new PatternRecognitionService(new MockPythonService(json));

        var result = await service.DetectAsync(CreateSampleCandles());

        Assert.False(result.IsSuccessful);
        Assert.Contains("tslearn", result.ErrorMessage);
    }

    [Fact]
    public async Task FullPipeline_InsufficientData_ReturnsEmptySuccessWithoutCallingPython()
    {
        // Only 5 candles, minWindow is 20 — should return empty without calling Python
        var service = new PatternRecognitionService(new MockPythonService("SHOULD_NOT_BE_CALLED"));

        var result = await service.DetectAsync(CreateSampleCandles(5), minWindow: 20);

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public async Task FullPipeline_MalformedJson_ReturnsFailure()
    {
        // Test resilience against malformed JSON from Python
        var service = new PatternRecognitionService(new MockPythonService("{invalid json}"));

        var result = await service.DetectAsync(CreateSampleCandles());

        // Should return failure, not throw
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task FullPipeline_PatternWithAllFields_MapsCorrectly()
    {
        var json = @"{
            ""status"": ""ok"",
            ""result"": {
                ""patterns"": [{
                    ""name"": ""InverseHeadAndShoulders"",
                    ""probability"": 0.9123,
                    ""startIndex"": 20,
                    ""endIndex"": 55
                }]
            }
        }";
        var service = new PatternRecognitionService(new MockPythonService(json));

        var result = await service.DetectAsync(CreateSampleCandles());

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Patterns);
        var pattern = result.Patterns[0];
        Assert.Equal("InverseHeadAndShoulders", pattern.Name);
        Assert.Equal(0.9123, pattern.Probability, precision: 4);
        Assert.Equal(20, pattern.StartIndex);
        Assert.Equal(55, pattern.EndIndex);
    }
}
