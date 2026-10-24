using System.Reflection;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Which <see cref="IStockAnalyzerSettings"/> properties sa_get_settings may return (plan S-17, A-11). Every property of the
/// interface is in exactly one of two lists: <see cref="Allowed"/> (returned) or <see cref="IntentionallyOmitted"/> (never
/// returned). A test fails when the interface gains a property that is in neither list, so each new setting needs a conscious
/// decision. A string-typed property whose name matches <see cref="ForbiddenNameRule"/> (paths, keys, tokens, endpoints) must not
/// be allowed, whatever the lists say.
/// </summary>
internal static class McpSettingsExposure
{
    public sealed record Entry(string Property, string Section, string JsonName);

    public static IReadOnlyList<Entry> Allowed { get; } = new[]
    {
        new Entry(nameof(IStockAnalyzerSettings.DefaultSymbol), "chart", "defaultSymbol"),
        new Entry(nameof(IStockAnalyzerSettings.ScreenerMaxParallelism), "screener", "maxParallelism"),
        new Entry(nameof(IStockAnalyzerSettings.ScreenerMaxConditionOffset), "screener", "maxConditionOffset"),
        new Entry(nameof(IStockAnalyzerSettings.ScreenerMaxFilterTreeDepth), "screener", "maxFilterTreeDepth"),
        new Entry(nameof(IStockAnalyzerSettings.ScreenerMaxFilterTreeNodes), "screener", "maxFilterTreeNodes"),
        new Entry(nameof(IStockAnalyzerSettings.BacktestMaxConditionOffset), "backtest", "maxConditionOffset"),
        new Entry(nameof(IStockAnalyzerSettings.BacktestMaxConditionTreeDepth), "backtest", "maxConditionTreeDepth"),
        new Entry(nameof(IStockAnalyzerSettings.BacktestMaxConditionTreeNodes), "backtest", "maxConditionTreeNodes"),
        new Entry(nameof(IStockAnalyzerSettings.ZigzagThresholdPercent), "marketStructure", "zigzagThresholdPercent"),
        new Entry(nameof(IStockAnalyzerSettings.PredictionWindowSize), "prediction", "windowSize"),
        new Entry(nameof(IStockAnalyzerSettings.PredictionFeatureMode), "prediction", "featureMode"),
    };

    /// <summary>Properties that are deliberately not returned: Python and pipe plumbing, file locations, colors, UI tuning, model internals.</summary>
    public static IReadOnlyList<string> IntentionallyOmitted { get; } = new[]
    {
        nameof(IStockAnalyzerSettings.PythonPath),
        nameof(IStockAnalyzerSettings.PythonScriptDirectory),
        nameof(IStockAnalyzerSettings.PythonServerScriptName),
        nameof(IStockAnalyzerSettings.PythonMaxRetries),
        nameof(IStockAnalyzerSettings.PythonBackoffMs),
        nameof(IStockAnalyzerSettings.PythonHealthCheckIntervalMs),
        nameof(IStockAnalyzerSettings.PipeConnectPollIntervalMs),
        nameof(IStockAnalyzerSettings.SyncTimeoutMinutes),
        nameof(IStockAnalyzerSettings.PythonEssentialPackages),
        nameof(IStockAnalyzerSettings.DisposeWaitMs),
        nameof(IStockAnalyzerSettings.PythonRequestTimeoutMs),
        nameof(IStockAnalyzerSettings.PythonMaxResponseBytes),
        nameof(IStockAnalyzerSettings.PythonMaxTimeoutRetries),
        nameof(IStockAnalyzerSettings.PythonImportProbeTimeout),
        nameof(IStockAnalyzerSettings.PythonUpdateCheckTimeout),
        nameof(IStockAnalyzerSettings.PythonUpdateCheckRequestTimeout),
        nameof(IStockAnalyzerSettings.RenkoUpColor),
        nameof(IStockAnalyzerSettings.RenkoDownColor),
        nameof(IStockAnalyzerSettings.KagiUpColor),
        nameof(IStockAnalyzerSettings.KagiDownColor),
        nameof(IStockAnalyzerSettings.PnfUpColor),
        nameof(IStockAnalyzerSettings.PnfDownColor),
        nameof(IStockAnalyzerSettings.ScreeningDataPath),
        nameof(IStockAnalyzerSettings.DefaultScreenerSymbols),
        nameof(IStockAnalyzerSettings.PipeName),
        nameof(IStockAnalyzerSettings.PipeConnectionTimeoutMs),
        nameof(IStockAnalyzerSettings.BacktestMaxConditionGroupNameLength),
        nameof(IStockAnalyzerSettings.BacktestConditionDragStartDistance),
        nameof(IStockAnalyzerSettings.ScreenerMaxFilterGroupNameLength),
        nameof(IStockAnalyzerSettings.ScreenerConditionDragStartDistance),
        nameof(IStockAnalyzerSettings.ScatterMaxRegisteredMetrics),
        nameof(IStockAnalyzerSettings.ScatterMaxMetricOffset),
        nameof(IStockAnalyzerSettings.ScatterMaxParallelism),
        nameof(IStockAnalyzerSettings.ScatterInputDebounceMilliseconds),
        nameof(IStockAnalyzerSettings.DrawingInteraction),
        nameof(IStockAnalyzerSettings.PatternRecognitionMinWindow),
        nameof(IStockAnalyzerSettings.PatternRecognitionMaxWindow),
        nameof(IStockAnalyzerSettings.PatternRecognitionWindowStep),
        nameof(IStockAnalyzerSettings.PatternRecognitionDefaultThreshold),
        nameof(IStockAnalyzerSettings.CircuitBreakerMinimumThroughput),
        nameof(IStockAnalyzerSettings.CircuitBreakerFailureRatio),
        nameof(IStockAnalyzerSettings.CircuitBreakerBreakDurationMs),
        nameof(IStockAnalyzerSettings.CircuitBreakerSamplingDurationMs),
        nameof(IStockAnalyzerSettings.PredictionModelPath),
        nameof(IStockAnalyzerSettings.PredictionTargetType),
        nameof(IStockAnalyzerSettings.PredictionFeatureSpec),
        nameof(IStockAnalyzerSettings.PredictionMaxComposedChannels),
        nameof(IStockAnalyzerSettings.PredictionMaxComposedTensorSizeMB),
        nameof(IStockAnalyzerSettings.PredictionConfidenceThreshold),
        nameof(IStockAnalyzerSettings.PredictionInputNodeName),
        nameof(IStockAnalyzerSettings.PredictionOutputNodeName),
        nameof(IStockAnalyzerSettings.PredictionClassLabels),
        nameof(IStockAnalyzerSettings.PredictionRetryMaxAttempts),
        nameof(IStockAnalyzerSettings.PredictionRetryBaseDelayMs),
        nameof(IStockAnalyzerSettings.PredictionRetryMaxDelayMs),
        nameof(IStockAnalyzerSettings.LocaleResourcePath),
    };

    /// <summary>String-typed properties with such a name hold locations or credentials and are never exposed.</summary>
    public static Regex ForbiddenNameRule { get; } = new("(path|dir|folder|key|token|secret|password|url|endpoint)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The section names in the order of <see cref="Allowed"/>.</summary>
    public static IReadOnlyList<string> Sections { get; } = Allowed.Select(e => e.Section).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The public properties of the settings interface (the set the two lists must cover exactly).</summary>
    public static IReadOnlyList<PropertyInfo> InterfaceProperties { get; } =
        typeof(IStockAnalyzerSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
