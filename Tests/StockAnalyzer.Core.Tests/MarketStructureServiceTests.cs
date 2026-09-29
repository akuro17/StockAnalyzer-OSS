using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.MathUtils;
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
        private readonly string _overlayResponse;

        public MockMarketStructurePythonService(string overlayResponse = "{}")
        {
            _overlayResponse = overlayResponse;
        }

        public bool IsInitializing => false;
        public Task InitializeAsync(System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task InitializeExternalProcessAsync() => Task.CompletedTask;
        public Task<string> PingExternalProcessAsync() => Task.FromResult("pong");
        public Task<string> SendCandlesAsync(List<CandleData> candles) => Task.FromResult("ready");
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics) => Task.FromResult("{}");
        public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => Task.FromResult("{}");
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, IReadOnlyList<double>? volatility = null)
        {
            LastVolatility = volatility;
            LastUseStructural = useStructural;
            return Task.FromResult(_overlayResponse);
        }

        public IReadOnlyList<double>? LastVolatility { get; private set; }
        public bool LastUseStructural { get; private set; }
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

    /// <summary>Deterministic random-walk candles with volatility clustering (a strictly linear series has no return variance to estimate).</summary>
    private static List<CandleData> CreateNoisyCandles(int count)
    {
        ulong state = 424242;
        double Uniform()
        {
            state = unchecked(6364136223846793005UL * state + 1442695040888963407UL);
            return ((state >> 11) + 0.5) / (double)(1UL << 53);
        }

        var candles = new List<CandleData>(count);
        double price = 100.0;
        double scale = 0.01;
        for (int i = 0; i < count; i++)
        {
            double z = Math.Sqrt(-2.0 * Math.Log(Uniform())) * Math.Cos(2.0 * Math.PI * Uniform());
            scale = 0.004 + 0.6 * scale + 0.25 * Math.Abs(0.01 * z);
            price *= Math.Exp(scale * z);
            decimal close = (decimal)price;
            candles.Add(new CandleData(DateTime.Today.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000));
        }
        return candles;
    }

    private const string ErrorResponse = @"{
        ""status"": ""error"",
        ""error"": ""Insufficient data: need >= 60 candles, got 10""
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

    #region StructuralDTW Service Integration Tests

    [Fact]
    public async Task CalculateStructuralDtwAsync_ComputesNatively()
    {
        var service = new MarketStructureService(new MockMarketStructurePythonService());
        var candles = CreateNoisyCandles(400);

        var result = await service.CalculateStructuralDtwAsync(candles, topK: 5, threshold: 0.3, futureSteps: 20);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.InRange(result.DominantPeriod, IndicatorDefaultConstants.StructuralDtwMinDominantPeriod, candles.Count / IndicatorDefaultConstants.StructuralDtwMaxDominantPeriodDivisor);
        Assert.Equal(result.DominantPeriod, result.DtwWindow);
        Assert.InRange(result.Matches.Count, 0, 5);
        Assert.All(result.Matches, m => Assert.Equal(m.EndIndex - m.StartIndex + 1, result.DtwWindow));
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_UsesTheNativeEgarchVolatilityForTheDistancePenalty()
    {
        var service = new MarketStructureService(new MockMarketStructurePythonService());
        var candles = CreateNoisyCandles(400);
        double[] closes = candles.Select(c => (double)c.Close).ToArray();
        double[] mids = candles.Select(c => ((double)c.High + (double)c.Low) / 2.0).ToArray();
        double[]? volatility = MarketStructureService.ComputeStructuralVolatility(candles);
        Assert.NotNull(volatility);

        var expected = StructuralDtwMath.Calculate(closes, mids, volatility, 5, 0.3, 20, ChartConstants.DtwDefaultWarpingRadius);
        var actual = await service.CalculateStructuralDtwAsync(candles);

        Assert.Equal(expected.QueryVolatility, actual.QueryVolatility);
        Assert.Equal(expected.Matches.Select(m => (m.StartIndex, m.Distance)), actual.Matches.Select(m => (m.StartIndex, m.Distance)));
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_HistoryWithoutUsableEgarchEstimate_FallsBackToRollingVolatility()
    {
        var service = new MarketStructureService(new MockMarketStructurePythonService());
        var flat = Enumerable.Range(0, 200).Select(i => new CandleData(DateTime.Today.AddDays(i), 100m, 100.5m, 99.5m, 100m, 1000)).ToList();
        Assert.Null(MarketStructureService.ComputeStructuralVolatility(flat));

        var result = await service.CalculateStructuralDtwAsync(flat);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_InsufficientData_ReturnsFailure()
    {
        var service = new MarketStructureService(new MockMarketStructurePythonService());

        var result = await service.CalculateStructuralDtwAsync(CreateTestCandles(10));

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    [Fact]
    public async Task CalculateStructuralDtwAsync_NullCandles_ReturnsFailure()
    {
        var service = new MarketStructureService(new MockMarketStructurePythonService());

        var result = await service.CalculateStructuralDtwAsync(null!);

        Assert.False(result.IsSuccessful);
    }

    #endregion

    #region Model Validation (structural DTW)

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
        var mockService = new MockMarketStructurePythonService(ValidOverlayResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(100);

        var result = await service.SearchSimilarPatternsAsync(candles, queryLength: 30, futureSteps: 20);

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Patterns);
        Assert.Equal(30, result.QueryLength);
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_Structural_SendsOneVolatilityValuePerCandle()
    {
        var mock = new MockMarketStructurePythonService(ValidOverlayResponse);
        var service = new MarketStructureService(mock);
        var candles = CreateNoisyCandles(300);

        var result = await service.SearchSimilarPatternsAsync(candles, queryLength: 30, futureSteps: 20, useStructural: true);

        Assert.True(result.IsSuccessful);
        Assert.True(mock.LastUseStructural);
        Assert.NotNull(mock.LastVolatility);
        Assert.Equal(candles.Count, mock.LastVolatility!.Count);
        Assert.All(mock.LastVolatility, v => Assert.True(double.IsFinite(v) && v > 0.0));
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_NotStructural_SendsNoVolatility()
    {
        var mock = new MockMarketStructurePythonService(ValidOverlayResponse);
        var service = new MarketStructureService(mock);

        await service.SearchSimilarPatternsAsync(CreateTestCandles(300), queryLength: 30, futureSteps: 20, useStructural: false);

        Assert.False(mock.LastUseStructural);
        Assert.Null(mock.LastVolatility);
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_StructuralWithUnusableHistory_FallsBackToUnfilteredSearch()
    {
        var mock = new MockMarketStructurePythonService(ValidOverlayResponse);
        var service = new MarketStructureService(mock);
        var flat = Enumerable.Range(0, 300)
            .Select(i => new CandleData(DateTime.Today.AddDays(i), 100m, 100m, 100m, 100m, 1000))
            .ToList();

        var result = await service.SearchSimilarPatternsAsync(flat, queryLength: 30, futureSteps: 20, useStructural: true);

        Assert.True(result.IsSuccessful);
        Assert.False(mock.LastUseStructural);
        Assert.Null(mock.LastVolatility);
    }

    [Fact]
    public void ComputeStructuralVolatility_AlignsWithCandles_AndRepeatsTheFirstValue()
    {
        var candles = CreateNoisyCandles(200);

        double[]? volatility = MarketStructureService.ComputeStructuralVolatility(candles);

        Assert.NotNull(volatility);
        Assert.Equal(candles.Count, volatility!.Length);
        Assert.Equal(volatility[0], volatility[1]);
        Assert.All(volatility, v => Assert.True(double.IsFinite(v) && v > 0.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ComputeStructuralVolatility_TooShortHistory_ReturnsNull(int count)
    {
        Assert.Null(MarketStructureService.ComputeStructuralVolatility(CreateTestCandles(count)));
    }

    [Fact]
    public async Task SearchSimilarPatternsAsync_InsufficientData_ReturnsFailure()
    {
        var mockService = new MockMarketStructurePythonService(ValidOverlayResponse);
        var service = new MarketStructureService(mockService);
        var candles = CreateTestCandles(10);

        var result = await service.SearchSimilarPatternsAsync(candles, queryLength: 30, futureSteps: 20);

        Assert.False(result.IsSuccessful);
        Assert.Contains("Insufficient data", result.ErrorMessage);
    }

    #endregion

    #region Model Validation

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
