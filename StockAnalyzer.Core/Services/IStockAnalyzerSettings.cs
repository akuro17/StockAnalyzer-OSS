using System.Collections.Generic;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Application settings interface for externalizing hardcoded values.
/// </summary>
public interface IStockAnalyzerSettings
{
    // Python Environment
    string? PythonPath { get; }
    string PythonScriptDirectory { get; }
    string PythonServerScriptName { get; }
    int PythonMaxRetries { get; }
    int PythonBackoffMs { get; }
    int PythonHealthCheckIntervalMs { get; }
    int PipeConnectPollIntervalMs { get; }
    int SyncTimeoutMinutes { get; }
    IReadOnlyList<string> PythonEssentialPackages { get; }
    int DisposeWaitMs { get; }

    // Chart Defaults
    string DefaultSymbol { get; }

    // Chart Colors (Hex)
    string RenkoUpColor { get; }
    string RenkoDownColor { get; }
    
    string KagiUpColor { get; }
    string KagiDownColor { get; }

    string PnfUpColor { get; }
    string PnfDownColor { get; }

    // Reverse Watch Phase Colors
    string GetReverseWatchPhaseColor(int phase);

    // SmartScreener
    string? ScreeningDataPath { get; }

    // Screener
    IReadOnlyList<string> DefaultScreenerSymbols { get; }

    // Infrastructure
    string PipeName { get; }
    int PipeConnectionTimeoutMs { get; }
    int ScreenerMaxParallelism { get; }

    // Market Structure (DTW, ZigZag)
    decimal ZigzagThresholdPercent { get; }

    // Pattern Recognition
    int PatternRecognitionMinWindow { get; }
    int PatternRecognitionMaxWindow { get; }
    int PatternRecognitionWindowStep { get; }
    double PatternRecognitionDefaultThreshold { get; }

    // Resilience (Circuit Breaker)
    int CircuitBreakerMinimumThroughput { get; }
    double CircuitBreakerFailureRatio { get; }
    int CircuitBreakerBreakDurationMs { get; }
    int CircuitBreakerSamplingDurationMs { get; }

    // AI Prediction
    string PredictionModelPath { get; }
    int PredictionWindowSize { get; }

    // Localization
    string? LocaleResourcePath { get; }
}
