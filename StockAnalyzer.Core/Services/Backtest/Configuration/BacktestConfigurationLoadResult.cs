using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

public enum BacktestConfigurationLoadStatus
{
    /// <summary>A valid, current-schema file was found and loaded.</summary>
    Loaded,

    /// <summary>No file exists yet; caller should apply its own built-in defaults (spec §5.5 "fileなし → 既定値適用" — not an error).</summary>
    NoFileDefaultsApplied,

    /// <summary>
    /// File exists but is corrupt, JSON null, an unknown/future SchemaVersion, or contains an
    /// undefined enum value. Spec §5.5: show an error, keep the current UI/original file untouched,
    /// never auto-overwrite.
    /// </summary>
    Error,
}

public sealed class BacktestConfigurationLoadResult
{
    public BacktestConfigurationLoadStatus Status { get; }
    public BacktestConfigurationDto? Configuration { get; }
    public string? ErrorMessage { get; }

    private BacktestConfigurationLoadResult(BacktestConfigurationLoadStatus status, BacktestConfigurationDto? configuration, string? errorMessage)
    {
        Status = status;
        Configuration = configuration;
        ErrorMessage = errorMessage;
    }

    public static BacktestConfigurationLoadResult Loaded(BacktestConfigurationDto configuration) =>
        new(BacktestConfigurationLoadStatus.Loaded, configuration, errorMessage: null);

    public static BacktestConfigurationLoadResult NoFileDefaultsApplied() =>
        new(BacktestConfigurationLoadStatus.NoFileDefaultsApplied, configuration: null, errorMessage: null);

    public static BacktestConfigurationLoadResult Error(string message) =>
        new(BacktestConfigurationLoadStatus.Error, configuration: null, errorMessage: message);
}
