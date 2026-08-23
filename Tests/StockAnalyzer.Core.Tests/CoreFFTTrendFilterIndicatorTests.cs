using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    [Collection("PythonIpc")]
    public class CoreFFTTrendFilterIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreFFTTrendFilterIndicatorTests()
        {
            _testData = new List<CoreCandleData>();
            for (int i = 0; i < 20; i++)
            {
                _testData.Add(new CoreCandleData(new System.DateTime(2023, 1, 1).AddDays(i), 100 + i, 110 + i, 95 + i, 105 + i, 1000));
            }
        }

        [Fact]
        public void CalculateCore_ThrowsNotSupportedException()
        {
            var indicator = new CoreFFTTrendFilterIndicator();
            Assert.Throws<System.NotSupportedException>(() => indicator.Calculate(_testData));
        }

        [Fact]
        public void IsOverlay_IsTrue()
        {
            // FFT Trend Filter reconstructs a price-scale line, so it must overlay the main
            // chart (unlike FFTCycle/FourierTransform, which are sub-panel indicators).
            var indicator = new CoreFFTTrendFilterIndicator();
            Assert.True(indicator.IsOverlay);
        }

        [Fact]
        public async Task CalculateAsync_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreFFTTrendFilterIndicator();
            var mockPythonService = new FftTrendFilterMockPythonService();
            var result = await indicator.CalculateAsync(new List<CoreCandleData>(), new CoreExecutionContext(mockPythonService));

            Assert.True(result.IsSuccessful);
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public async Task CalculateAsync_WithValidData_ReturnsSuccessAndValues()
        {
            var indicator = new CoreFFTTrendFilterIndicator();
            var mockPythonService = new FftTrendFilterMockPythonService();
            var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(mockPythonService));

            Assert.True(result.IsSuccessful);
            Assert.Equal(_testData.Count, indicator.Values.Count);
            // First value in the mock response is deliberately null to mirror an unfilled window.
            Assert.Null(indicator.Values[0]);
            Assert.True(indicator.Values.Skip(1).All(v => v.HasValue));
        }

        [Fact]
        public async Task CalculateAsync_WithRealPython_Succeeds()
        {
            var settings = new RealPythonTestSettings("testffttrendfilterindicatorpipe");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var indicator = new CoreFFTTrendFilterIndicator { WindowSize = 4, NumHarmonics = 2 };
            var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(pythonService));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(_testData.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        private class RealPythonTestSettings : IStockAnalyzerSettings
        {
            private readonly string _pipeName;
            public RealPythonTestSettings(string pipeName) => _pipeName = pipeName;

            public string? PythonPath => null;
            public string PythonScriptDirectory => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "StockAnalyzer.Core", "Scripts"));
            public string PythonServerScriptName => "server.py";
            public int PythonMaxRetries => 3;
            public int PythonBackoffMs => 100;
            public int PythonHealthCheckIntervalMs => 1000;
            public int PipeConnectPollIntervalMs => 100;
            public int SyncTimeoutMinutes => 1;
            public IReadOnlyList<string> PythonEssentialPackages => new[] { "pandas", "numpy", "scipy" };
            public int DisposeWaitMs => 100;
            public string DefaultSymbol => "MSFT";
            public string RenkoUpColor => "#00FF00";
            public string RenkoDownColor => "#FF0000";
            public string KagiUpColor => "#00FF00";
            public string KagiDownColor => "#FF0000";
            public string PnfUpColor => "#00FF00";
            public string PnfDownColor => "#FF0000";
            public string GetReverseWatchPhaseColor(int phase) => "#FFFFFF";
            public string? ScreeningDataPath => null;
            public IReadOnlyList<string> DefaultScreenerSymbols => new List<string>().AsReadOnly();
            public string PipeName => _pipeName;
            public int PipeConnectionTimeoutMs => 5000;
            public int ScreenerMaxParallelism => 4;
            public decimal ZigzagThresholdPercent => 5m;
            public int PatternRecognitionMinWindow => 10;
            public int PatternRecognitionMaxWindow => 100;
            public int PatternRecognitionWindowStep => 5;
            public double PatternRecognitionDefaultThreshold => 0.5;
            public int CircuitBreakerMinimumThroughput => 10;
            public double CircuitBreakerFailureRatio => 0.5;
            public int CircuitBreakerBreakDurationMs => 1000;
            public int CircuitBreakerSamplingDurationMs => 1000;
            public string PredictionModelPath => "";
            public int PredictionWindowSize => 30;
            public string? LocaleResourcePath => null;
        }

        private class FftTrendFilterMockPythonService : MockPythonServiceBase
        {
            public override Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics)
            {
                // Generate dummy response matching input size (20): first bar null (unfilled window), rest = 101.5
                var values = new List<string> { "null" };
                values.AddRange(Enumerable.Repeat("101.5", 19));
                var trend = string.Join(",", values);
                return Task.FromResult($"{{\"status\":\"ok\",\"result\":{{\"trend\":[{trend}]}}}}");
            }
        }
    }
}
