using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    [Collection("PythonIpc")]
    public class CoreFFTCycleIndicatorTests
    {
        private readonly List<CoreCandleData> _testData;

        public CoreFFTCycleIndicatorTests()
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
            var indicator = new CoreFFTCycleIndicator();
            Assert.Throws<System.NotSupportedException>(() => indicator.Calculate(_testData));
        }

        [Fact]
        public async Task CalculateAsync_WithEmptyData_ReturnsEmpty()
        {
            var indicator = new CoreFFTCycleIndicator();
            var mockPythonService = new FftCycleMockPythonService();
            var result = await indicator.CalculateAsync(new List<CoreCandleData>(), new CoreExecutionContext(mockPythonService));

            Assert.True(result.IsSuccessful);
            Assert.Empty(indicator.Values);
        }

        [Fact]
        public async Task CalculateAsync_WithValidData_ReturnsSuccessAndValues()
        {
            var indicator = new CoreFFTCycleIndicator();
            var mockPythonService = new FftCycleMockPythonService();
            var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(mockPythonService));

            Assert.True(result.IsSuccessful);
            Assert.Equal(_testData.Count, indicator.Values.Count);
            // First value in the mock response is deliberately null to mirror an unfilled window.
            Assert.Null(indicator.Values[0]);
            Assert.True(indicator.Values.Skip(1).All(v => v.HasValue));

            Assert.Equal(_testData.Count, indicator.CycleStrength.Count);
            Assert.Null(indicator.CycleStrength[0]);
            Assert.True(indicator.CycleStrength.Skip(1).All(v => v.HasValue));

            Assert.Equal(_testData.Count, indicator.Oscillator.Count);
            Assert.Null(indicator.Oscillator[0]);
            Assert.True(indicator.Oscillator.Skip(1).All(v => v.HasValue));
        }

        [Fact]
        public async Task CalculateAsync_WithRealPython_Succeeds()
        {
            var settings = new RealPythonTestSettings("testfftcycleindicatorpipe1");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var indicator = new CoreFFTCycleIndicator { WindowSize = 4 };
            var result = await indicator.CalculateAsync(_testData, new CoreExecutionContext(pythonService));

            Assert.True(result.IsSuccessful, result.ErrorMessage);
            Assert.Equal(_testData.Count, result.GetSeries(IndicatorResult.MainSeriesName).Count);
        }

        [Fact]
        public async Task CalculateAsync_WithRealPython_SineWaveCycleStrength_ExceedsWhiteNoise()
        {
            // A single fixed-noise realization can occasionally produce a max/mean spectral
            // ratio above small absolute thresholds by chance (extreme-value statistics over
            // ~32 bins), so this asserts the discriminative property that actually matters:
            // a genuine cycle's strength must clearly exceed noise's, rather than pinning
            // either side to an arbitrary absolute number.
            const int windowSize = 64;
            const int barCount = 200;
            var start = new System.DateTime(2023, 1, 1);

            var sineSettings = new RealPythonTestSettings("testfftcycleindicatorpipe2");
            var sinePythonService = new PythonService(sineSettings);
            await sinePythonService.InitializeAsync();
            await sinePythonService.InitializeExternalProcessAsync();

            const int knownPeriod = 16;
            var sineCandles = new List<CoreCandleData>();
            for (int i = 0; i < barCount; i++)
            {
                decimal close = 100m + (decimal)System.Math.Sin(2 * System.Math.PI * i / knownPeriod) * 10m;
                sineCandles.Add(new CoreCandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            var sineIndicator = new CoreFFTCycleIndicator { WindowSize = windowSize };
            var sineResult = await sineIndicator.CalculateAsync(sineCandles, new CoreExecutionContext(sinePythonService));
            Assert.True(sineResult.IsSuccessful, sineResult.ErrorMessage);
            var sineStrength = sineIndicator.CycleStrength[barCount - 1];
            Assert.True(sineStrength.HasValue);

            var noiseSettings = new RealPythonTestSettings("testfftcycleindicatorpipe3");
            var noisePythonService = new PythonService(noiseSettings);
            await noisePythonService.InitializeAsync();
            await noisePythonService.InitializeExternalProcessAsync();

            var random = new System.Random(42);
            var noiseCandles = new List<CoreCandleData>();
            for (int i = 0; i < barCount; i++)
            {
                decimal close = 100m + (decimal)(random.NextDouble() * 10.0 - 5.0);
                noiseCandles.Add(new CoreCandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            var noiseIndicator = new CoreFFTCycleIndicator { WindowSize = windowSize };
            var noiseResult = await noiseIndicator.CalculateAsync(noiseCandles, new CoreExecutionContext(noisePythonService));
            Assert.True(noiseResult.IsSuccessful, noiseResult.ErrorMessage);
            var noiseStrength = noiseIndicator.CycleStrength[barCount - 1];
            Assert.True(noiseStrength.HasValue);

            Assert.True(sineStrength!.Value > noiseStrength!.Value * 1.5m,
                $"Expected sine-wave CycleStrength ({sineStrength}) to clearly exceed white-noise CycleStrength ({noiseStrength})");
        }

        [Fact]
        public async Task CalculateAsync_WithRealPython_SineWave_OscillatorMatchesKnownPeriod()
        {
            var settings = new RealPythonTestSettings("testfftcycleindicatorpipe4");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            const int knownPeriod = 16;
            const int windowSize = 64;
            const int barCount = 200;
            const decimal knownAmplitude = 10m;
            var candles = new List<CoreCandleData>();
            var start = new System.DateTime(2023, 1, 1);
            for (int i = 0; i < barCount; i++)
            {
                decimal close = 100m + (decimal)(System.Math.Sin(2 * System.Math.PI * i / knownPeriod) * (double)knownAmplitude);
                candles.Add(new CoreCandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            var indicator = new CoreFFTCycleIndicator { WindowSize = windowSize };
            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(pythonService));
            Assert.True(result.IsSuccessful, result.ErrorMessage);

            // Only bars once the window is filled carry an Oscillator value.
            var filled = indicator.Oscillator.Skip(windowSize - 1).Select(v => v!.Value).ToList();
            Assert.True(filled.All(v => v != 0m), "Oscillator should not be trivially zero once the window is filled.");

            // Amplitude should land in a sane, bounded range around roughly half the input
            // amplitude: the Hanning window applied before the FFT has a coherent gain of
            // ~0.5, so the normalized peak is not expected to exactly match the input's
            // amplitude, but it must not collapse to ~0 nor explode past it.
            var maxAbs = filled.Max(v => System.Math.Abs(v));
            Assert.InRange(maxAbs, knownAmplitude * 0.2m, knownAmplitude * 0.8m);

            // Zero-crossings of the oscillator should recur roughly every half of the known
            // period (a cosine crosses zero twice per cycle), proving the extracted phase
            // tracks the actual input periodicity rather than being arbitrary noise.
            var crossingIndices = new List<int>();
            for (int i = 1; i < filled.Count; i++)
            {
                if ((filled[i - 1] > 0m && filled[i] <= 0m) || (filled[i - 1] < 0m && filled[i] >= 0m))
                {
                    crossingIndices.Add(i);
                }
            }
            Assert.True(crossingIndices.Count >= 3, $"Expected multiple zero-crossings, got {crossingIndices.Count}");

            var gaps = new List<int>();
            for (int i = 1; i < crossingIndices.Count; i++)
            {
                gaps.Add(crossingIndices[i] - crossingIndices[i - 1]);
            }
            double averageGap = gaps.Average();
            Assert.InRange(averageGap, (knownPeriod / 2.0) - 3.0, (knownPeriod / 2.0) + 3.0);
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

        private class FftCycleMockPythonService : MockPythonServiceBase
        {
            public override Task<string> CalculateFftCycleAsync(int windowSize = ChartConstants.FftCycleDefaultWindowSize)
            {
                // Generate dummy response matching input size (20): first bar null (unfilled window), rest = 16.0 / 3.5
                var values = new List<string> { "null" };
                values.AddRange(Enumerable.Repeat("16.0", 19));
                var cycle = string.Join(",", values);

                var strengthValues = new List<string> { "null" };
                strengthValues.AddRange(Enumerable.Repeat("3.5", 19));
                var strength = string.Join(",", strengthValues);

                var oscillatorValues = new List<string> { "null" };
                oscillatorValues.AddRange(Enumerable.Repeat("0.7", 19));
                var oscillator = string.Join(",", oscillatorValues);

                return Task.FromResult($"{{\"status\":\"ok\",\"result\":{{\"cycle\":[{cycle}],\"strength\":[{strength}],\"oscillator\":[{oscillator}]}}}}");
            }
        }
    }
}
