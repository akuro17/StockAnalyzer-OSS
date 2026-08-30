using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    // Regression test: a ~1369-candle dataset (matching a real ticker's Daily history) used
    // to fail with "Received empty response." because server.py's read_pipe() treated
    // ERROR_MORE_DATA (a benign "partial message, call ReadFile again" signal on a
    // PIPE_TYPE_MESSAGE pipe) as fatal, aborting the chunked Arrow candle transfer any time
    // the payload exceeded the pipe's 65536-byte buffer (~1300+ candles). Indicator
    // calculation would then fail outright with no data reaching the chart.
    [Collection("PythonIpc")]
    public class ReproFFTCycleLargeRealisticDatasetTests
    {
        [Fact]
        public async Task CalculateAsync_With1369RealisticCandles_ProducesNonNullValues()
        {
            var settings = new RealPythonTestSettings("reprofftcyclelargepipe1");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var candles = new List<CoreCandleData>();
            var start = new System.DateTime(2021, 1, 1);
            var rnd = new System.Random(7);
            decimal price = 2000m;
            for (int i = 0; i < 1369; i++)
            {
                price += (decimal)(rnd.NextDouble() * 40 - 20);
                if (price < 100m) price = 100m;
                candles.Add(new CoreCandleData(start.AddDays(i), price, price + 30m, price - 30m, price, 100000));
            }

            var indicator = new CoreFFTCycleIndicator(); // default WindowSize = 64
            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(pythonService));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(candles.Count, indicator.Values.Count);

            int nonNullCount = indicator.Values.Count(v => v.HasValue);
            Assert.True(nonNullCount > 0, $"Expected non-null cycle values once the 64-bar window fills (candle count={candles.Count}), but got 0 non-null out of {indicator.Values.Count}.");
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
            public StockAnalyzer.Core.Models.PredictionFeatureMode PredictionFeatureMode => StockAnalyzer.Core.Models.PredictionFeatureMode.OhlcvMinMax;
            public float PredictionConfidenceThreshold => 0.5f;
            public string? PredictionInputNodeName => null;
            public string? PredictionOutputNodeName => null;
            public System.Collections.Generic.IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
            public int PredictionRetryMaxAttempts => 3;
            public int PredictionRetryBaseDelayMs => 50;
            public int PredictionRetryMaxDelayMs => 500;
            public string? LocaleResourcePath => null;
        }
    }
}
