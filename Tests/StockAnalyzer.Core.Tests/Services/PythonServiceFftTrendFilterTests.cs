using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// Verifies the Python-side "calculate_fft_trend_filter" handler in server.py directly via
    /// IPythonService, independent of CoreFFTTrendFilterIndicator (added in a later task).
    /// </summary>
    [Collection("PythonIpc")]
    public class PythonServiceFftTrendFilterTests
    {
        private const int WindowSize = 64;
        private const int NumHarmonics = 4;

        [Fact]
        public async Task CalculateFftTrendFilterAsync_WithNoisySineWave_SmoothsAndTracksBaseSignal()
        {
            var settings = new RealPythonTestSettings("testffttrendfilterpipe1");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            // Base signal: a low-frequency sine (period 32 -> bin 2, well within the 4
            // low-frequency bins kept). Noise: a Nyquist-frequency square wave (period 2 ->
            // bin 32, far outside the kept bins), so a correct low-pass filter must suppress
            // it while tracking the base sine.
            const int basePeriod = 32;
            const decimal baseAmplitude = 10m;
            const decimal noiseAmplitude = 3m;
            const int barCount = 200;
            var start = new DateTime(2023, 1, 1);

            var noisyCandles = new List<CandleData>();
            var baseValues = new List<decimal>();
            for (int i = 0; i < barCount; i++)
            {
                decimal baseValue = 100m + (decimal)Math.Sin(2 * Math.PI * i / basePeriod) * baseAmplitude;
                decimal noise = (i % 2 == 0) ? noiseAmplitude : -noiseAmplitude;
                decimal close = baseValue + noise;
                baseValues.Add(baseValue);
                noisyCandles.Add(new CandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            await pythonService.SendCandlesAsync(noisyCandles);
            var responseJson = await pythonService.CalculateFftTrendFilterAsync(WindowSize, NumHarmonics);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("status", out var status) && status.GetString() == "error",
                root.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error");

            var trendArray = root.GetProperty("result").GetProperty("trend").EnumerateArray().ToList();
            Assert.Equal(barCount, trendArray.Count);

            for (int i = 0; i < WindowSize - 1; i++)
            {
                Assert.Equal(JsonValueKind.Null, trendArray[i].ValueKind);
            }

            var trend = trendArray.Skip(WindowSize - 1).Select(v => v.GetDouble()).ToList();
            var noisyPrices = noisyCandles.Skip(WindowSize - 1).Select(c => (double)c.Close).ToList();
            var baseSignal = baseValues.Skip(WindowSize - 1).Select(v => (double)v).ToList();

            // 1) Bar-to-bar noise must be clearly reduced relative to the raw noisy price.
            double StdOfDiffs(IReadOnlyList<double> series)
            {
                var diffs = new List<double>();
                for (int i = 1; i < series.Count; i++) diffs.Add(series[i] - series[i - 1]);
                double mean = diffs.Average();
                return Math.Sqrt(diffs.Average(d => (d - mean) * (d - mean)));
            }
            double noisyStd = StdOfDiffs(noisyPrices);
            double trendStd = StdOfDiffs(trend);
            Assert.True(trendStd < noisyStd * 0.5,
                $"Expected filtered trend bar-to-bar std ({trendStd}) to be well below raw noisy price std ({noisyStd})");

            // 2) The filtered trend must closely track the underlying clean base sine wave.
            double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
            {
                double meanA = a.Average(), meanB = b.Average();
                double cov = 0, varA = 0, varB = 0;
                for (int i = 0; i < a.Count; i++)
                {
                    double da = a[i] - meanA, db = b[i] - meanB;
                    cov += da * db;
                    varA += da * da;
                    varB += db * db;
                }
                return cov / Math.Sqrt(varA * varB);
            }
            double correlation = Correlation(trend, baseSignal);
            Assert.True(correlation > 0.9, $"Expected trend to closely correlate with the base sine wave, got r={correlation}");
        }

        [Fact]
        public async Task CalculateFftTrendFilterAsync_WithEmptyData_ReturnsErrorNotException()
        {
            var settings = new RealPythonTestSettings("testffttrendfilterpipe2");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var responseJson = await pythonService.CalculateFftTrendFilterAsync(WindowSize, NumHarmonics);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("status", out var status) && status.GetString() == "error");
        }

        private class RealPythonTestSettings : IStockAnalyzerSettings
        {
            private readonly string _pipeName;
            public RealPythonTestSettings(string pipeName) => _pipeName = pipeName;

            public string? PythonPath => null;
            public string PythonScriptDirectory => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "StockAnalyzer.Core", "Scripts"));
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
