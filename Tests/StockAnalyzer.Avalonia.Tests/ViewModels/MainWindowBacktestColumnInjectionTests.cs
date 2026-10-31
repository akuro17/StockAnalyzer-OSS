using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// IoHydrationWebAiReview T7: the workspace capture reads the Backtest column settings through the injected
/// <see cref="IBacktestColumnSettingsManager"/> instead of constructing its own, so the registered singleton is the
/// one consulted (and a test can stand in for it).
/// </summary>
// Shares the static WeakReferenceMessenger.Default with other MainWindowViewModel/TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class MainWindowBacktestColumnInjectionTests
{
    private sealed class FakeColumnSettingsManager : IBacktestColumnSettingsManager
    {
        public int LoadCalls { get; private set; }
        public BacktestColumnSettings? Settings { get; init; }

        public BacktestColumnSettings? Load()
        {
            LoadCalls++;
            return Settings;
        }

        public Task<BacktestColumnSettings?> LoadAsync() => Task.FromResult(Load());
        public Task SaveAsync(BacktestColumnSettings settings) => Task.CompletedTask;
        public Task SaveColumnsAsync(IEnumerable<string>? tradeColumns, IEnumerable<string>? metricNames) => Task.CompletedTask;
    }

    [Fact]
    public void CaptureWorkspaceSettings_UsesTheInjectedBacktestColumnManager()
    {
        var manager = new FakeColumnSettingsManager
        {
            Settings = new BacktestColumnSettings
            {
                TradeVisibleColumns = new List<string> { "InjectedTradeColumn", "Second" },
                MetricVisibleNames = new List<string> { "InjectedMetric" },
            },
        };
        var h = MainWindowViewModelFactory.Create(backtestColumnSettingsManager: manager);
        using var vm = h.Vm;
        var settings = new WorkspaceSettings();

        vm.CaptureWorkspaceSettings(settings);

        Assert.Equal(1, manager.LoadCalls);
        Assert.Equal(new[] { "InjectedTradeColumn", "Second" }, settings.BacktestTradeVisibleColumns);
        Assert.Equal(new[] { "InjectedMetric" }, settings.BacktestMetricVisibleNames);
    }

    [Fact]
    public void CaptureWorkspaceSettings_WhenTheInjectedManagerHasNothingStored_LeavesTheColumnListsUntouched()
    {
        var manager = new FakeColumnSettingsManager { Settings = null };
        var h = MainWindowViewModelFactory.Create(backtestColumnSettingsManager: manager);
        using var vm = h.Vm;
        var settings = new WorkspaceSettings
        {
            BacktestTradeVisibleColumns = new List<string> { "Keep" },
        };

        vm.CaptureWorkspaceSettings(settings);

        Assert.Equal(1, manager.LoadCalls);
        Assert.Equal(new[] { "Keep" }, settings.BacktestTradeVisibleColumns);
    }
}
