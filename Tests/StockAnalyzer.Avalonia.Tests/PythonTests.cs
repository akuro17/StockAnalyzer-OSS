using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Tests
{
    public class PythonTests
    {
        [Fact]
        public async Task PythonProcessManager_CanStartAndConnect()
        {
            var settings = new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings();
            await using var svc = new PythonService(settings);
            
            try 
            {
                await svc.InitializeExternalProcessAsync();
                Assert.True(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("PYTHON FAIL: " + ex.Message);
                throw;
            }
        }

        [Fact]
        public async Task PythonProcessManager_WithInvalidCustomPythonPath_ThrowsFileNotFoundException()
        {
            var baseSettings = new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings();
            var settings = new CustomPythonSettings(baseSettings, "C:\\invalid_python_path_xyz\\python.exe");
            
            await using var manager = new PythonProcessManager(settings);
            
            await Assert.ThrowsAsync<System.IO.FileNotFoundException>(async () => 
            {
                await manager.StartAsync();
            });
        }

        private class CustomPythonSettings : IStockAnalyzerSettings
        {
            private readonly IStockAnalyzerSettings _base;
            public CustomPythonSettings(IStockAnalyzerSettings baseSettings, string pythonPath)
            {
                _base = baseSettings;
                PythonPath = pythonPath;
            }
            public string? PythonPath { get; }
            public string PythonScriptDirectory => _base.PythonScriptDirectory;
            public string PythonServerScriptName => _base.PythonServerScriptName;
            public int PythonMaxRetries => _base.PythonMaxRetries;
            public int PythonBackoffMs => _base.PythonBackoffMs;
            public int PythonHealthCheckIntervalMs => _base.PythonHealthCheckIntervalMs;
            public int PipeConnectPollIntervalMs => _base.PipeConnectPollIntervalMs;
            public int SyncTimeoutMinutes => _base.SyncTimeoutMinutes;
            public IReadOnlyList<string> PythonEssentialPackages => _base.PythonEssentialPackages;
            public int DisposeWaitMs => _base.DisposeWaitMs;
            public string DefaultSymbol => _base.DefaultSymbol;
            public string RenkoUpColor => _base.RenkoUpColor;
            public string RenkoDownColor => _base.RenkoDownColor;
            public string KagiUpColor => _base.KagiUpColor;
            public string KagiDownColor => _base.KagiDownColor;
            public string PnfUpColor => _base.PnfUpColor;
            public string PnfDownColor => _base.PnfDownColor;
            public string GetReverseWatchPhaseColor(int phase) => _base.GetReverseWatchPhaseColor(phase);
            public string? ScreeningDataPath => _base.ScreeningDataPath;
            public IReadOnlyList<string> DefaultScreenerSymbols => _base.DefaultScreenerSymbols;
            public string PipeName => _base.PipeName;
            public int PipeConnectionTimeoutMs => _base.PipeConnectionTimeoutMs;
            public int ScreenerMaxParallelism => _base.ScreenerMaxParallelism;
            public decimal ZigzagThresholdPercent => _base.ZigzagThresholdPercent;
            public int PatternRecognitionMinWindow => _base.PatternRecognitionMinWindow;
            public int PatternRecognitionMaxWindow => _base.PatternRecognitionMaxWindow;
            public int PatternRecognitionWindowStep => _base.PatternRecognitionWindowStep;
            public double PatternRecognitionDefaultThreshold => _base.PatternRecognitionDefaultThreshold;
            public int CircuitBreakerMinimumThroughput => _base.CircuitBreakerMinimumThroughput;
            public double CircuitBreakerFailureRatio => _base.CircuitBreakerFailureRatio;
            public int CircuitBreakerBreakDurationMs => _base.CircuitBreakerBreakDurationMs;
            public int CircuitBreakerSamplingDurationMs => _base.CircuitBreakerSamplingDurationMs;
            public string PredictionModelPath => _base.PredictionModelPath;
            public int PredictionWindowSize => _base.PredictionWindowSize;
            public string? LocaleResourcePath => _base.LocaleResourcePath;
        }
    }
}
