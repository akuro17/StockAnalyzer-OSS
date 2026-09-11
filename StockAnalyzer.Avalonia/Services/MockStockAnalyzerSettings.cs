using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// A dummy implementation of IStockAnalyzerSettings used exclusively for the Avalonia Design-Time Previewer
/// and unit tests that do not need to test the real configuration injection mechanism.
/// This prevents tight coupling of ViewModels to the huge StockAnalyzerSettings concrete constructor.
/// </summary>
public class MockStockAnalyzerSettings : IStockAnalyzerSettings
{
    // Python Environment
    public string? PythonPath => null;
    public string PythonScriptDirectory => "Scripts";
    public string PythonServerScriptName => "server.py";
    public int PythonMaxRetries => 3;
    public int PythonBackoffMs => 1000;
    public int PythonHealthCheckIntervalMs => 5000;
    public int PipeConnectPollIntervalMs => 100;
    public int SyncTimeoutMinutes => 2;
    public IReadOnlyList<string> PythonEssentialPackages => new[] { "pandas", "numpy" };
    public int DisposeWaitMs => 1000;

    // Chart Defaults
    public string DefaultSymbol => "AAPL";

    // Chart Colors (Hex)
    public string RenkoUpColor => "Green";
    public string RenkoDownColor => "Red";
    
    public string KagiUpColor => "Green";
    public string KagiDownColor => "Red";

    public string PnfUpColor => "Green";
    public string PnfDownColor => "Red";

    // Reverse Watch Phase Colors
    public string GetReverseWatchPhaseColor(int phase)
    {
         return phase switch
         {
             1 => "#CCFF99", // Lime Green (Phase 1)
             2 => "#00CC00", // Green (Phase 2)
             3 => "#006600", // Dark Green (Phase 3)
             4 => "#FFD700", // Yellow (Phase 4)
             5 => "#FF9999", // Salmon (Phase 5)
             6 => "#FF0000", // Red (Phase 6)
             7 => "#8B0000", // Dark Red (Phase 7)
             8 => "#607D8B", // Blue Gray (Phase 8)
             _ => "#808080"
         };
    }

    // SmartScreener
    public string? ScreeningDataPath => null;

    // Screener
    public IReadOnlyList<string> DefaultScreenerSymbols => new[] { "AAPL", "MSFT", "GOOGL" };

    // Infrastructure
    public string PipeName => "StockAnalyzerPipe_Mock";
    public int PipeConnectionTimeoutMs => 5000;
    public int ScreenerMaxParallelism => 4;

    // Market Structure (DTW, ZigZag)
    public decimal ZigzagThresholdPercent => 5.0m;

    // Pattern Recognition
    public int PatternRecognitionMinWindow => 20;
    public int PatternRecognitionMaxWindow => 60;
    public int PatternRecognitionWindowStep => 5;
    public double PatternRecognitionDefaultThreshold => 0.5;

    // Resilience (Circuit Breaker)
    public int CircuitBreakerMinimumThroughput => 3;
    public double CircuitBreakerFailureRatio => 0.5;
    public int CircuitBreakerBreakDurationMs => 30000;
    public int CircuitBreakerSamplingDurationMs => 60000;

    // AI Prediction
    public string PredictionModelPath => "Models/mock_model.onnx";
    public int PredictionWindowSize => 60;
    public PredictionFeatureMode PredictionFeatureMode => PredictionFeatureMode.OhlcvMinMax;
    public float PredictionConfidenceThreshold => 0.5f;
    public string? PredictionInputNodeName => null;
    public string? PredictionOutputNodeName => null;
    public IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
    public int PredictionRetryMaxAttempts => 3;
    public int PredictionRetryBaseDelayMs => 50;
    public int PredictionRetryMaxDelayMs => 500;

    // Localization
    public string? LocaleResourcePath => null;
}
