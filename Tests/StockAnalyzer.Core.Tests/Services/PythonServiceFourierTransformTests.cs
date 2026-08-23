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
    /// Verifies the Python-side "calculate_fourier_transform" (Goertzel) handler in server.py
    /// directly via IPythonService, independent of CoreFourierTransformIndicator (added in a
    /// later task).
    /// </summary>
    [Collection("PythonIpc")]
    public class PythonServiceFourierTransformTests
    {
        private const int TargetPeriod = 20;

        [Fact]
        public async Task CalculateFourierTransformAsync_WithMatchingSineWave_DetectsKnownAmplitude()
        {
            var settings = new RealPythonTestSettings("testfouriertransformpipe1");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            // A sine wave whose period exactly matches TargetPeriod and whose amplitude is
            // known, so the Goertzel output can be checked against ground truth.
            const decimal knownAmplitude = 10m;
            const int barCount = 100;
            var candles = new List<CandleData>();
            var start = new DateTime(2023, 1, 1);
            for (int i = 0; i < barCount; i++)
            {
                decimal close = 100m + (decimal)(Math.Sin(2 * Math.PI * i / TargetPeriod) * (double)knownAmplitude);
                candles.Add(new CandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            await pythonService.SendCandlesAsync(candles);
            var responseJson = await pythonService.CalculateFourierTransformAsync(TargetPeriod);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            Assert.False(root.TryGetProperty("status", out var status) && status.GetString() == "error",
                root.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error");

            var amplitudeArray = root.GetProperty("result").GetProperty("amplitude").EnumerateArray().ToList();
            Assert.Equal(barCount, amplitudeArray.Count);

            // Bars before the window is filled must be null (no look-ahead / fabricated values).
            for (int i = 0; i < TargetPeriod - 1; i++)
            {
                Assert.Equal(JsonValueKind.Null, amplitudeArray[i].ValueKind);
            }

            var lastValue = amplitudeArray[barCount - 1];
            Assert.NotEqual(JsonValueKind.Null, lastValue.ValueKind);
            double detectedAmplitude = lastValue.GetDouble();
            Assert.InRange(detectedAmplitude, (double)knownAmplitude - 1.0, (double)knownAmplitude + 1.0);
        }

        [Fact]
        public async Task CalculateFourierTransformAsync_WithEmptyData_ReturnsErrorNotException()
        {
            var settings = new RealPythonTestSettings("testfouriertransformpipe2");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var responseJson = await pythonService.CalculateFourierTransformAsync(TargetPeriod);

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
            public string? LocaleResourcePath => null;
        }
    }
}
