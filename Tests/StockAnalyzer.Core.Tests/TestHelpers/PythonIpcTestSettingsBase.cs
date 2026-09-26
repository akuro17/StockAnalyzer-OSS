using System.Collections.Generic;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>Settings stub for tests that drive the real server.py over the named pipe; override only what a test needs to vary.</summary>
public class PythonIpcTestSettingsBase : IStockAnalyzerSettings
{
    public virtual string? PythonPath => null;
    public virtual string PythonScriptDirectory => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "StockAnalyzer.Core", "Scripts"));
    public virtual string PythonServerScriptName => "server.py";
    public virtual int PythonMaxRetries => 3;
    public virtual int PythonBackoffMs => 100;
    public virtual int PythonHealthCheckIntervalMs => 1000;
    public virtual int PipeConnectPollIntervalMs => 100;
    public virtual int SyncTimeoutMinutes => 1;
    public virtual IReadOnlyList<string> PythonEssentialPackages => new[] { "pandas", "numpy" };
    public virtual int DisposeWaitMs => 100;
    public virtual string DefaultSymbol => "MSFT";
    public virtual string RenkoUpColor => "#00FF00";
    public virtual string RenkoDownColor => "#FF0000";
    public virtual string KagiUpColor => "#00FF00";
    public virtual string KagiDownColor => "#FF0000";
    public virtual string PnfUpColor => "#00FF00";
    public virtual string PnfDownColor => "#FF0000";
    public virtual string GetReverseWatchPhaseColor(int phase) => "#FFFFFF";
    public virtual string? ScreeningDataPath => null;
    public virtual IReadOnlyList<string> DefaultScreenerSymbols => new List<string>().AsReadOnly();
    public virtual string PipeName => "testipcsafetypipe";
    public virtual int PipeConnectionTimeoutMs => 5000;
    public virtual int ScreenerMaxParallelism => 4;
    public virtual decimal ZigzagThresholdPercent => 5m;
    public virtual int PatternRecognitionMinWindow => 10;
    public virtual int PatternRecognitionMaxWindow => 100;
    public virtual int PatternRecognitionWindowStep => 5;
    public virtual double PatternRecognitionDefaultThreshold => 0.5;
    public virtual int CircuitBreakerMinimumThroughput => 10;
    public virtual double CircuitBreakerFailureRatio => 0.5;
    public virtual int CircuitBreakerBreakDurationMs => 1000;
    public virtual int CircuitBreakerSamplingDurationMs => 1000;
    public virtual string PredictionModelPath => "";
    public virtual int PredictionWindowSize => 30;
    public virtual StockAnalyzer.Core.Models.PredictionFeatureMode PredictionFeatureMode => StockAnalyzer.Core.Models.PredictionFeatureMode.OhlcvMinMax;
    public virtual float PredictionConfidenceThreshold => 0.5f;
    public virtual string? PredictionInputNodeName => null;
    public virtual string? PredictionOutputNodeName => null;
    public virtual IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
    public virtual int PredictionRetryMaxAttempts => 3;
    public virtual int PredictionRetryBaseDelayMs => 50;
    public virtual int PredictionRetryMaxDelayMs => 500;
    public virtual string? LocaleResourcePath => null;

    // Overrides of default interface members (Python IPC limits).
    public virtual int PythonRequestTimeoutMs => new StockAnalyzer.Core.Models.Settings.PythonIpcSettings().RequestTimeoutMs;
    public virtual int PythonMaxResponseBytes => new StockAnalyzer.Core.Models.Settings.PythonIpcSettings().MaxResponseBytes;
    public virtual int PythonMaxTimeoutRetries => new StockAnalyzer.Core.Models.Settings.PythonIpcSettings().MaxTimeoutRetries;
}
