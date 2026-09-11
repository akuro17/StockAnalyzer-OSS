using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests.Services
{
    [Collection("PythonIpc")]
    public class PythonServiceSsaTests
    {
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

        [Fact]
        public async Task CalculateSsaAsync_ReturnsValidJsonWithSsaArray()
        {
            var settings = new RealPythonTestSettings("testpythonservicesapipe");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var candles = new List<CandleData>();
            for (int i = 0; i < 30; i++)
            {
                decimal p = 100m + i * 1.5m;
                candles.Add(new CandleData(new DateTime(2023, 1, 1).AddDays(i), p, p + 1m, p - 1m, p, 1000));
            }

            await pythonService.SendCandlesAsync(candles);
            string responseJson = await pythonService.CalculateSsaAsync(windowSize: 10, embeddingDimension: 4, numComponents: 2);

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("result", out var resultElement));
            Assert.True(resultElement.TryGetProperty("ssa", out var ssaArray));
            Assert.Equal(30, ssaArray.GetArrayLength());
        }
    }
}
