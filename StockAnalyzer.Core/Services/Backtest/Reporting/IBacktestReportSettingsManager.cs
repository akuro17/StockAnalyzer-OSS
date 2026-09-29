using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

public enum BacktestReportSettingsLoadStatus
{
    Loaded,
    BuiltInFallback,
}

public sealed record BacktestReportSettingsLoadResult(
    BacktestReportDefaults Defaults,
    BacktestReportSettingsLoadStatus Status);

public interface IBacktestReportSettingsManager
{
    /// <summary>Returns the persisted defaults, or <see cref="BacktestReportDefaults.BuiltIn"/> if nothing has been saved yet.</summary>
    Task<BacktestReportDefaults> LoadAsync();

    async Task<BacktestReportSettingsLoadResult> LoadWithStatusAsync() =>
        new(await LoadAsync().ConfigureAwait(false), BacktestReportSettingsLoadStatus.Loaded);

    Task SaveAsync(BacktestReportDefaults defaults);
}
