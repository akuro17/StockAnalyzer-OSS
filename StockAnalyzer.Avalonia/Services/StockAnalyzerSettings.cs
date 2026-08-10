using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

public class StockAnalyzerSettings : IStockAnalyzerSettings
{
    private readonly IConfiguration _configuration;
    private readonly PythonSettings _pythonSettings;
    private readonly ChartDefaultSettings _chartDefaults;
    private readonly PredictionSettings _predictionSettings;
    private readonly ILogger<StockAnalyzerSettings> _logger;

    public StockAnalyzerSettings(
        IConfiguration configuration,
        IOptions<PythonSettings> pythonOptions,
        IOptions<ChartDefaultSettings> chartDefaultOptions,
        IOptions<PredictionSettings> predictionOptions,
        IOptions<ScreenerSettings> screenerOptions,
        IOptions<SmartScreenerSettings> smartScreenerOptions,
        IOptions<InfrastructureSettings> infrastructureOptions,
        IOptions<MarketStructureSettings> marketStructureOptions,
        IOptions<PatternRecognitionSettings> patternRecognitionOptions,
        IOptions<ResilienceSettings> resilienceOptions,
        IOptions<LocalizationSettings> localizationOptions,
        ILogger<StockAnalyzerSettings>? logger = null)
    {
        _logger = logger ?? NullLogger<StockAnalyzerSettings>.Instance;
        _configuration = configuration;

        _pythonSettings = pythonOptions.Value;
        _pythonSettings.Validate();

        _chartDefaults = chartDefaultOptions.Value;
        _chartDefaults.Validate();

        _predictionSettings = predictionOptions.Value;
        _predictionSettings.Validate();

        _screenerSettings = screenerOptions.Value;
        _screenerSettings.Validate();

        _smartScreenerSettings = smartScreenerOptions.Value;
        _smartScreenerSettings.Validate();

        _infrastructureSettings = infrastructureOptions.Value;
        _infrastructureSettings.Validate();

        _marketStructureSettings = marketStructureOptions.Value;
        _marketStructureSettings.Validate();

        _patternRecognitionSettings = patternRecognitionOptions.Value;
        _patternRecognitionSettings.Validate();

        _resilienceSettings = resilienceOptions.Value;
        _resilienceSettings.Validate();

        _localizationSettings = localizationOptions.Value;
        _localizationSettings.Validate();
    }

    private readonly ScreenerSettings _screenerSettings;
    private readonly SmartScreenerSettings _smartScreenerSettings;
    private readonly InfrastructureSettings _infrastructureSettings;
    private readonly MarketStructureSettings _marketStructureSettings;
    private readonly PatternRecognitionSettings _patternRecognitionSettings;
    private readonly ResilienceSettings _resilienceSettings;
    private readonly LocalizationSettings _localizationSettings;

    // Python settings (via IOptions<PythonSettings>)
    public string? PythonPath => _pythonSettings.PythonPath;
    public string PythonScriptDirectory => _pythonSettings.ScriptDirectory;
    public string PythonServerScriptName => _pythonSettings.ServerScriptName;
    public int PythonMaxRetries => _pythonSettings.MaxRetries;
    public int PythonBackoffMs => _pythonSettings.BackoffMs;
    public int PythonHealthCheckIntervalMs => _pythonSettings.HealthCheckIntervalMs;
    public int PipeConnectPollIntervalMs => _pythonSettings.PipeConnectPollIntervalMs;
    public int SyncTimeoutMinutes => _pythonSettings.SyncTimeoutMinutes;
    public IReadOnlyList<string> PythonEssentialPackages => _pythonSettings.EssentialPackages;
    public int DisposeWaitMs => _pythonSettings.DisposeWaitMs;

    // Chart defaults (via IOptions<ChartDefaultSettings>)
    public string DefaultSymbol => _chartDefaults.DefaultSymbol;

    // Chart Colors (still via IConfiguration as these are not in scope of this fix)
    public string RenkoUpColor => _configuration["Chart:Renko:UpColor"] ?? "Green";
    public string RenkoDownColor => _configuration["Chart:Renko:DownColor"] ?? "Red";

    public string KagiUpColor => _configuration["Chart:Kagi:UpColor"] ?? "Green";
    public string KagiDownColor => _configuration["Chart:Kagi:DownColor"] ?? "Red";

    public string PnfUpColor => _configuration["Chart:Pnf:UpColor"] ?? "Green";
    public string PnfDownColor => _configuration["Chart:Pnf:DownColor"] ?? "Red";

    public string GetReverseWatchPhaseColor(int phase)
    {
         // Default Fallback Colors matching ChartViewModel defaults
         string defaultColor = phase switch
         {
             1 => "#88CC00", // Deeper Lime Green (Phase 1)
             2 => "#00AA00", // Standard Green (Phase 2)
             3 => "#005500", // Dark Green (Phase 3)
             4 => "#E6B800", // Deep Yellow (Phase 4)
             5 => "#E65C5C", // Deep Salmon/Light Red (Phase 5)
             6 => "#EE0000", // Red (Phase 6)
             7 => "#8B0000", // Dark Red (Phase 7)
             8 => "#607D8B", // Blue Gray (Phase 8)
             _ => "#808080"
         };

         return _configuration[$"Chart:ReverseWatch:Phase{phase}Color"] ?? defaultColor;
    }

    public string? ScreeningDataPath => _smartScreenerSettings.ScreeningDataPath;

    // Screener
    public IReadOnlyList<string> DefaultScreenerSymbols => _screenerSettings.DefaultSymbols;

    // Infrastructure
    public string PipeName => _infrastructureSettings.PipeName;
    public int PipeConnectionTimeoutMs => _infrastructureSettings.PipeConnectionTimeoutMs;
    public int ScreenerMaxParallelism => _infrastructureSettings.ScreenerMaxParallelism;

    // Market Structure (DTW, ZigZag)
    public decimal ZigzagThresholdPercent => _marketStructureSettings.ZigzagThresholdPercent;

    // Pattern Recognition
    public int PatternRecognitionMinWindow => _patternRecognitionSettings.MinWindow;
    public int PatternRecognitionMaxWindow => _patternRecognitionSettings.MaxWindow;
    public int PatternRecognitionWindowStep => _patternRecognitionSettings.WindowStep;
    public double PatternRecognitionDefaultThreshold => _patternRecognitionSettings.DefaultThreshold;

    // Resilience (Circuit Breaker)
    public int CircuitBreakerMinimumThroughput => _resilienceSettings.CircuitBreaker.MinimumThroughput;
    public double CircuitBreakerFailureRatio => _resilienceSettings.CircuitBreaker.FailureRatio;
    public int CircuitBreakerBreakDurationMs => _resilienceSettings.CircuitBreaker.BreakDurationMs;
    public int CircuitBreakerSamplingDurationMs => _resilienceSettings.CircuitBreaker.SamplingDurationMs;

    // AI Prediction
    public string PredictionModelPath => _predictionSettings.ModelPath;
    public int PredictionWindowSize => _predictionSettings.WindowSize;

    // Localization
    public string? LocaleResourcePath => _localizationSettings.ResourcePath;
}
