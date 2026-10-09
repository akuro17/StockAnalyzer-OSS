using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Configuration;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>In-memory <see cref="IBacktestDefaultSettingsManager"/>: loads <see cref="Settings"/> (the built-in defaults at first) and keeps what was saved.</summary>
public sealed class FakeBacktestDefaultSettingsManager : IBacktestDefaultSettingsManager
{
    public BacktestDefaultSettings Settings { get; private set; } = BacktestDefaultSettings.BuiltIn;

    public Task<BacktestDefaultSettingsLoadResult> LoadAsync()
        => Task.FromResult(new BacktestDefaultSettingsLoadResult(Settings, BacktestDefaultSettingsLoadStatus.Loaded));

    public Task SaveAsync(BacktestDefaultSettings settings)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
