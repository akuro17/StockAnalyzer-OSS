using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// Stub settings for SmartScreenerService tests.
    /// Returns null for ScreeningDataPath to trigger FindRepoRoot fallback.
    /// </summary>
    internal class StubScreenerSettings : IStockAnalyzerSettings
    {
        public string? PythonPath => null;
        public string PythonScriptDirectory => "Scripts";
        public string PythonServerScriptName => "server.py";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1000;
        public int PythonHealthCheckIntervalMs => 5000;
        public string DefaultSymbol => "MSFT";
        public string RenkoUpColor => "Green";
        public string RenkoDownColor => "Red";
        public string KagiUpColor => "Green";
        public string KagiDownColor => "Red";
        public string PnfUpColor => "Green";
        public string PnfDownColor => "Red";
        public string? ScreeningDataPath => null;
        public string PipeName => "StockAnalyzerPipe";
        public int PipeConnectionTimeoutMs => 5000;
        public int ScreenerMaxParallelism => 10;
        // Pattern Recognition
        public int PatternRecognitionMinWindow => 20;
        public int PatternRecognitionMaxWindow => 60;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;
        // Market Structure
        public decimal ZigzagThresholdPercent => 5.0m;
        // Localization
        public string? LocaleResourcePath => null;
        public string GetReverseWatchPhaseColor(int phase) => "#808080";
        public IReadOnlyList<string> DefaultScreenerSymbols => System.Array.Empty<string>();
        // Resilience (Circuit Breaker)
        public int CircuitBreakerMinimumThroughput => 3;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public int PipeConnectPollIntervalMs => 100;
        public int DisposeWaitMs => 1000;
        public int SyncTimeoutMinutes => 2;
        public IReadOnlyList<string> PythonEssentialPackages => new[] { "setuptools", "wheel", "polars", "pandas", "scipy", "yfinance", "pyarrow", "pandas-ta", "scikit-learn", "arch", "statsmodels", "pywin32", "tslearn" };
        public string PredictionModelPath => "Models/trend_predictor.onnx";
        public int PredictionWindowSize => 10;
        public StockAnalyzer.Core.Models.PredictionFeatureMode PredictionFeatureMode => StockAnalyzer.Core.Models.PredictionFeatureMode.OhlcvMinMax;
        public float PredictionConfidenceThreshold => 0.5f;
        public string? PredictionInputNodeName => null;
        public string? PredictionOutputNodeName => null;
        public System.Collections.Generic.IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
        public int PredictionRetryMaxAttempts => 3;
        public int PredictionRetryBaseDelayMs => 50;
        public int PredictionRetryMaxDelayMs => 500;
    }

    [Collection("PythonIpc")]
    public class SmartScreenerServiceTests
    {
        [Fact]
        public async Task ScreenAsync_ShouldReturnSymbols_WhenCriteriaIsMatched()
        {
            // Arrange
            var settings = new StubScreenerSettings();
            var pythonService = new PythonService(settings);
            var service = new SmartScreenerService(pythonService, settings);

            // Ensure JSON file exists or mock it (but we depend on real file in the service for now)
            // A meaningful test requires the jsonDataPath to be valid.
            // In the service constructor, it tries to find the repo root.
            
            // Act
            // Criteria: Symbol is 'AAPL' (should exist in S&P 500)
            // Or Price > 0
            var result = await service.ScreenAsync("price > 0");

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result);
            // Verify at least some known symbols are present
            Assert.Contains("AAPL", result);
            Assert.Contains("MSFT", result);
        }

        [Fact]
        public async Task ScreenAsync_ShouldFilterCorrectly()
        {
            // Arrange
            var settings = new StubScreenerSettings();
            var pythonService = new PythonService(settings);
            var service = new SmartScreenerService(pythonService, settings);

            // Act
            // Highly specific filter that might match few or none, or specific one
            // Let's try to filter by symbol to be sure
            var result = await service.ScreenAsync("symbol == 'AAPL'");

            // Assert
            Assert.Single(result);
            Assert.Equal("AAPL", result[0]);
        }
        
        [Fact]
        public async Task ScreenAsync_ShouldHandleComplexQueries()
        {
             // Arrange
            var settings = new StubScreenerSettings();
            var pythonService = new PythonService(settings);
            var service = new SmartScreenerService(pythonService, settings);

            // Act
            // Example of using indicator columns. Note that in JSON keys are 'RSI_14', etc.
            // We need to ensure capitalization matches.
            // Let's try a query that involves indicators if possible.
            // Based on latest_screening_data.json sample: "RSI_14" exists.
            var result = await service.ScreenAsync("RSI_14 > 70");

            // Assert
            Assert.NotNull(result);
            // We can't guarantee count without knowing data, but it shouldn't throw.
        }
    }
}
