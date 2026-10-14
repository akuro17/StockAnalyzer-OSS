using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>In-memory <see cref="IBacktestDefaultSettingsManager"/>: loads <see cref="Settings"/> (the built-in defaults at first) and keeps what was saved.</summary>
public sealed class FakeBacktestDefaultSettingsManager : IBacktestDefaultSettingsManager
{
    private readonly BacktestDefaultSettingsLoadStatus _status;

    /// <summary>The status the load reports: <c>Loaded</c> by default, <c>BuiltInFallback</c> to model a missing or invalid settings file.</summary>
    public FakeBacktestDefaultSettingsManager(BacktestDefaultSettingsLoadStatus status = BacktestDefaultSettingsLoadStatus.Loaded, BacktestDefaultSettings? settings = null)
    {
        _status = status;
        Settings = settings ?? BacktestDefaultSettings.BuiltIn;
    }

    public BacktestDefaultSettings Settings { get; private set; }

    public Task<BacktestDefaultSettingsLoadResult> LoadAsync()
        => Task.FromResult(new BacktestDefaultSettingsLoadResult(Settings, _status));

    public Task SaveAsync(BacktestDefaultSettings settings)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
