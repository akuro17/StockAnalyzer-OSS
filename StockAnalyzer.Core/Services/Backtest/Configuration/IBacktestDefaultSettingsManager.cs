using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

public enum BacktestDefaultSettingsLoadStatus
{
    Loaded,
    BuiltInFallback,
}

public sealed record BacktestDefaultSettingsLoadResult(
    BacktestDefaultSettings Settings,
    BacktestDefaultSettingsLoadStatus Status);

/// <summary>Persists the user-defined Backtest defaults (Settings > Backtest).</summary>
public interface IBacktestDefaultSettingsManager
{
    /// <summary>Returns the persisted defaults, or <see cref="BacktestDefaultSettings.BuiltIn"/> when the file is absent or invalid.</summary>
    Task<BacktestDefaultSettingsLoadResult> LoadAsync();

    /// <summary>Validates then persists. An invalid value throws <see cref="System.ArgumentException"/> and leaves the file untouched.</summary>
    Task SaveAsync(BacktestDefaultSettings settings);
}
