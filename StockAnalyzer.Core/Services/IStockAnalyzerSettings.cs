using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

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

    // Python IPC limits (defaults live in Models.Settings.PythonIpcSettings so every existing implementer keeps compiling unchanged)
    int PythonRequestTimeoutMs => new Models.Settings.PythonIpcSettings().RequestTimeoutMs;
    int PythonMaxResponseBytes => new Models.Settings.PythonIpcSettings().MaxResponseBytes;
    int PythonMaxTimeoutRetries => new Models.Settings.PythonIpcSettings().MaxTimeoutRetries;

    // Backtest
    /// <summary>
    /// Largest Offset a backtest condition may use (see <see cref="Models.Settings.BacktestSettings.MaxConditionOffset"/>). Defaulted here (default interface member,
    /// delegating to the POCO default) so every existing implementer keeps compiling unchanged.
    /// </summary>
    int BacktestMaxConditionOffset => new Models.Settings.BacktestSettings().MaxConditionOffset;

    // Drawing interaction resource bounds
    /// <summary>
    /// Undo/redo history and freehand stroke bounds (see <see cref="Models.Settings.DrawingInteractionSettings"/>). Defaulted here (default interface member,
    /// delegating to the POCO defaults) so every existing implementer keeps compiling unchanged.
    /// </summary>
    Models.Settings.DrawingInteractionSettings DrawingInteraction => new Models.Settings.DrawingInteractionSettings();

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
    PredictionFeatureMode PredictionFeatureMode { get; }

    /// <summary>
    /// Learning objective the configured model was trained for. Defaulted here (default
    /// interface member) so every existing implementer of this interface keeps compiling
    /// unchanged; only settings sources that actually support regression need to override it.
    /// </summary>
    TargetType PredictionTargetType => TargetType.Classification;
    /// <summary>
    /// Configured composed-feature channel spec when <see cref="PredictionFeatureMode"/> is
    /// <see cref="Models.PredictionFeatureMode.ComposedFeatures"/>; otherwise <see langword="null"/>.
    /// Defaulted to <see langword="null"/> here (default interface member) so every existing
    /// implementer of this large, long-lived interface keeps compiling unchanged; only settings
    /// sources that actually support composed features need to override it.
    /// </summary>
    FeatureSpec? PredictionFeatureSpec => null;

    /// <summary>
    /// D03 resource bound (StockAnalyzer/CLAUDE.md decision register): the maximum number of
    /// channels a composed-feature spec may carry, sourced from configuration (not a compiled
    /// constant) per the Change Policy's hardcoded-value prohibition. Defaulted here (default
    /// interface member) so every existing implementer of this interface keeps compiling
    /// unchanged; only settings sources that actually support composed features need to
    /// override it.
    /// </summary>
    int PredictionMaxComposedChannels => 1024;

    /// <summary>
    /// D03 resource bound: the maximum size, in megabytes, of one inference call's input
    /// tensor. See <see cref="PredictionMaxComposedChannels"/> for why this defaults here.
    /// </summary>
    int PredictionMaxComposedTensorSizeMB => 256;

    float PredictionConfidenceThreshold { get; }
    string? PredictionInputNodeName { get; }
    string? PredictionOutputNodeName { get; }
    IReadOnlyList<string> PredictionClassLabels { get; }
    int PredictionRetryMaxAttempts { get; }
    int PredictionRetryBaseDelayMs { get; }
    int PredictionRetryMaxDelayMs { get; }

    // Localization
    string? LocaleResourcePath { get; }
}
