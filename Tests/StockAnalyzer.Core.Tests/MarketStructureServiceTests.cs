using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class MarketStructureServiceTests
{
    #region MockPythonService
    private class MockMarketStructurePythonService : IPythonService
    {
        private readonly string _structuralDtwResponse;
        private readonly string _overlayResponse;

        public MockMarketStructurePythonService(string structuralDtwResponse, string overlayResponse = "{}")
        {
            _structuralDtwResponse = structuralDtwResponse;
            _overlayResponse = overlayResponse;
        }

        public bool IsInitializing => false;
        public Task InitializeAsync(System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task InitializeExternalProcessAsync() => Task.CompletedTask;
        public Task<string> PingExternalProcessAsync() => Task.FromResult("pong");
        public Task<string> SendCandlesAsync(List<CandleData> candles) => Task.FromResult("ready");
        public Task<string> CalculateEgarchAsync(int p = 1, int q = 1) => Task.FromResult("{}");
        public Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m) => Task.FromResult("{}");
        public Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize) => Task.FromResult("{}");
        public Task<string> CalculateFourierTransformAsync(int targetPeriod = ChartConstants.FourierTransformDefaultTargetPeriod) => Task.FromResult("{}");
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics) => Task.FromResult("{}");
        public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => Task.FromResult("{}");
        public Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha) => Task.FromResult("{}");
        public Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult(_structuralDtwResponse);
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult(_overlayResponse);
        public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius)
            => Task.FromResult("{}");
        public Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, System.Threading.CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<T> RunAsync<T>(Func<Python.Runtime.PyModule, T> func, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(default(T)!);
    }

    #endregion

    #region Test Data

    private static List<CandleData> CreateTestCandles(int count)
    {
        var startDate = DateTime.Today;
        return Enumerable.Range(0, count).Select(i => new CandleData(
            startDate.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + i, 1000
        )).ToList();
    }

    private const string ValidDtwResponse = @"{
        ""status"": ""ok"",
        ""result"": {
            ""dominantPeriod"": 20,
            ""dtwWindow"": 20,
            ""queryVolatility"": 1.5432,
            ""matches"": [
                {
                    ""distance"": 2.3456,
                    ""probability"": 0.8912,
                    ""startIndex"": 50,
                    ""endIndex"": 69,
                    ""futurePath"": [0.5, 1.2, -0.3, 0.8, 1.5]
                },
                {
                    ""distance"": 3.1234,
                    ""probability"": 0.7543,
                    ""startIndex"": 120,
                    ""endIndex"": 139,
                    ""futurePath"": [-0.2, 0.8, 1.1, 0.5, -0.1]
                }
            ]
        }
    }";

    private const string ErrorResponse = @"{
        ""status"": ""error"",
        ""error"": ""Insufficient data: need >= 60 candles, got 10""
    }";

    private const string EmptyMatchesResponse = @"{
        ""status"": ""ok"",
        ""result"": {
            ""dominantPeriod"": 15,
            ""dtwWindow"": 15,
            ""queryVolatility"": 0.8,
            ""matches"": []
        }
    }";

    private const string ValidOverlayResponse = @"{
        ""status"": ""ok"",
        ""result"": {
            ""queryLength"": 30,
            ""patterns"": [
                {
                    ""distance"": 1.5,
                    ""probability"": 0.92,
                    ""startIndex"": 40,
                    ""endIndex"": 69,
                    ""matchedPrices"": [100.0, 101.5, 103.0, 102.0, 104.5],
                    ""futureRawPrices"": [105.0, 106.2, 104.8, 107.3],
                    ""futurePercentChange"": [0.48, 1.62, 0.29, 2.68]
                }
            ]
        }
    }";

    private const string EmptyOverlayResponse = @"{
        ""status"": ""ok"",
        ""result"": {
            ""queryLength"": 30,
            ""patterns"": []
        }
    }";

    #endregion

    #region StructuralDTW ParseResponse Tests

    [Fact]
    public void ParseResponse_ValidJson_ReturnsSuccess()
    {
        var result = MarketStructureService.ParseResponse(ValidDtwResponse);

        Assert.True(result.IsSuccessful);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(20, result.DominantPeriod);
        Assert.Equal(20, result.DtwWindow);
        Assert.Equal(1.5432, result.QueryVolatility, 4);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void ParseResponse_ValidJson_ParsesFirstMatch()
    {
        var result = MarketStructureService.ParseResponse(ValidDtwResponse);

        var first = result.Matches[0];
        Assert.Equal(2.3456, first.Distance, 4);
        Assert.Equal(0.8912, first.Probability, 4);
        Assert.Equal(50, first.StartIndex);
        Assert.Equal(69, first.EndIndex);
        Assert.Equal(5, first.FuturePath.Count);
        Assert.Equal(0.5, first.FuturePath[0], 1);
        Assert.Equal(1.5, first.FuturePath[4], 1);
    }

    [Fact]
    public void ParseResponse_ErrorJson_ReturnsFailure()
    {
        var result = MarketStructureService.ParseResponse(ErrorResponse);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void ParseResponse_EmptyMatches_ReturnsSuccessWithNoPatterns()
    {
        var result = MarketStructureService.ParseResponse(EmptyMatchesResponse);

        Assert.True(result.IsSuccessful);
        Assert.Equal(15, result.DominantPeriod);
        Assert.Empty(result.Matches);
    }

    #endregion

    #region StructuralDTW Service Integration Tests

    [Fact]
    public async Task CalculateStructuralDtwAsync_WithMock_ReturnsExpectedResult()
    {
        var mockService = new MockMarketStructurePythonService(ValidDtwResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(100);

        var result = await service.CalculateStructuralDtwAsync(candles, topK: 5, threshold: 0.3, futureSteps: 20);

        Assert.True(result.IsSuccessful);
        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(20, result.DominantPeriod);
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_InsufficientData_ReturnsFailure()
    {
        var mockService = new MockMarketStructurePythonService(ValidDtwResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(10);

        var result = await service.CalculateStructuralDtwAsync(candles);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_NullCandles_ReturnsFailure()
    {
        var mockService = new MockMarketStructurePythonService(ValidDtwResponse);
        var service = new MarketStructureService(mockService);

        var result = await service.CalculateStructuralDtwAsync(null!);

        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_PythonError_ReturnsFailure()
    {
        var mockService = new MockMarketStructurePythonService(ErrorResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(100);

        var result = await service.CalculateStructuralDtwAsync(candles);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    #endregion

    #region Overlay ParseOverlayResponse Tests

    [Fact]
    public void ParseOverlayResponse_ValidJson_ReturnsSuccess()
    {
        var result = MarketStructureService.ParseOverlayResponse(ValidOverlayResponse);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.QueryLength);
        Assert.Single(result.Patterns);
    }

    [Fact]
    public void ParseOverlayResponse_ValidJson_ParsesPatternFields()
    {
        var result = MarketStructureService.ParseOverlayResponse(ValidOverlayResponse);
        var pattern = result.Patterns[0];

        Assert.Equal(1.5, pattern.Distance, 1);
        Assert.Equal(0.92, pattern.Probability, 2);
        Assert.Equal(40, pattern.StartIndex);
        Assert.Equal(69, pattern.EndIndex);
        Assert.Equal(5, pattern.MatchedPrices.Count);
        Assert.Equal(100.0, pattern.MatchedPrices[0], 1);
        Assert.Equal(4, pattern.FutureRawPrices.Count);
        Assert.Equal(105.0, pattern.FutureRawPrices[0], 1);
        Assert.Equal(4, pattern.FuturePercentChange.Count);
        Assert.Equal(0.48, pattern.FuturePercentChange[0], 2);
    }

    [Fact]
    public void ParseOverlayResponse_EmptyPatterns_ReturnsSuccessEmpty()
    {
        var result = MarketStructureService.ParseOverlayResponse(EmptyOverlayResponse);

        Assert.True(result.IsSuccessful);
        Assert.Equal(30, result.QueryLength);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void ParseOverlayResponse_Error_ReturnsFailure()
    {
        var result = MarketStructureService.ParseOverlayResponse(ErrorResponse);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    #endregion

    #region Overlay Service Integration Tests

    [Fact]
    public async Task SearchSimilarPatternsAsync_WithMock_ReturnsExpectedResult()
    {
        var mockService = new MockMarketStructurePythonService("{}", ValidOverlayResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(100);

        var result = await service.SearchSimilarPatternsAsync(candles, queryLength: 30, futureSteps: 20);

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Patterns);
        Assert.Equal(30, result.QueryLength);
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_InsufficientData_ReturnsFailure()
    {
        var mockService = new MockMarketStructurePythonService("{}", ValidOverlayResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(10);

        var result = await service.SearchSimilarPatternsAsync(candles, queryLength: 30, futureSteps: 20);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    #endregion

    #region Model Validation

    [Fact]
    public void StructuralDtwResult_Failure_HasDefaults()
    {
        var result = StructuralDtwResult.Failure("test error");

        Assert.False(result.IsSuccessful);
        Assert.Equal("test error", result.ErrorMessage);
        Assert.Equal(0, result.DominantPeriod);
        Assert.Equal(0, result.DtwWindow);
        Assert.Equal(0, result.QueryVolatility);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void SimilarPatternResult_DefaultFuturePath_IsEmpty()
    {
        var pattern = new SimilarPatternResult();
        Assert.NotNull(pattern.FuturePath);
        Assert.Empty(pattern.FuturePath);
    }

    [Fact]
    public void PatternOverlayResult_Failure_HasDefaults()
    {
        var result = PatternOverlayResult.Failure("overlay error");

        Assert.False(result.IsSuccessful);
        Assert.Equal("overlay error", result.ErrorMessage);
        Assert.Equal(0, result.QueryLength);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void OverlayPattern_DefaultArrays_AreEmpty()
    {
        var pattern = new OverlayPattern();
        Assert.NotNull(pattern.MatchedPrices);
        Assert.Empty(pattern.MatchedPrices);
        Assert.NotNull(pattern.FutureRawPrices);
        Assert.Empty(pattern.FutureRawPrices);
        Assert.NotNull(pattern.FuturePercentChange);
        Assert.Empty(pattern.FuturePercentChange);
    }

    #endregion
}
